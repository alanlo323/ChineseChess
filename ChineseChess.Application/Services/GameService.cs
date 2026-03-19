using ChineseChess.Application.Configuration;
using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Services;

public class GameService : IGameService, IDisposable
{
    private const int HintExplanationThinkingTreeDepth = 6;
    private readonly IAiEngine aiEngine;          // 紅方（或共用）引擎
    private IAiEngine? aiEngineBlack;             // 黑方引擎（獨立TT模式才有值）
    private readonly BookmarkManager bookmarkManager;
    private Board board;
    private GameMode currentMode;
    // 使用 int（0/1）搭配 Interlocked 做原子 check-and-set，避免 volatile bool 的 check-then-act 競態條件
    private int isThinkingFlag;
    private bool isThinking => Volatile.Read(ref isThinkingFlag) != 0;
    // 提示搜尋進行中旗標（volatile 確保跨執行緒可見性）
    private volatile bool isHintSearchingFlag;
    private SearchSettings redAiSettings  = new SearchSettings();
    private SearchSettings blackAiSettings = new SearchSettings();
    private CancellationTokenSource? aiCts;
    private CancellationTokenSource? smartHintCts;
    private readonly ManualResetEventSlim aiPauseSignal = new ManualResetEventSlim(true);
    private readonly IHintExplanationService? hintExplanationService;
    private SearchResult? latestHint;
    private string? latestHintFen;
    private int latestHintMoveCount = -1;
    private IReadOnlyList<MoveEvaluation>? latestMultiPvEvaluations;

    public IBoard CurrentBoard => board;
    public GameMode CurrentMode => currentMode;
    public bool IsThinking => isThinking; // 讀取 isThinkingFlag（透過私有 property）
    public bool IsHintSearching => isHintSearchingFlag;
    public Move? LastMove => board.TryGetLastMove(out var lastMove) ? lastMove : null;
    public bool IsSmartHintEnabled { get; set; } = true;
    public int SmartHintDepth { get; set; } = 2;
    public bool IsMultiPvHintEnabled { get; set; } = false;
    public int MultiPvCount { get; set; } = 3;
    public long LastSearchNodes => Interlocked.Read(ref lastSearchNodes);
    public long LastSearchNps => Interlocked.Read(ref lastSearchNps);
    public bool UseSharedTranspositionTable { get; set; } = true;
    public bool CopyRedTtToBlackAtStart { get; set; } = true;
    public PieceColor PlayerColor { get; set; } = PieceColor.Red;
    private long lastSearchNodes;
    private long lastSearchNps;
    private long completedGameNodes; // 歷史已完成搜尋的累計節點數（本局）

    // ─── WXF 重複局面歷史 ──────────────────────────────────────────────────
    // 首筆為種子條目（初始局面），其後每步著法追加一筆
    private readonly List<MoveRecord> wxfHistory = new();

    // ─── 走法歷史與重播 ────────────────────────────────────────────────────
    private readonly List<MoveHistoryEntry> moveHistory = new();
    private string initialFen = string.Empty;
    private ReplayState replayState = ReplayState.Live;
    private int replayCurrentStep;

    public IReadOnlyList<MoveHistoryEntry> MoveHistory => moveHistory;
    public string InitialFen => initialFen;
    public ReplayState ReplayState => replayState;
    public int ReplayCurrentStep => replayCurrentStep;

    public event Action? MoveHistoryChanged;
    public event Action? ReplayStateChanged;

    // ─── 提和相關欄位 ──────────────────────────────────────────────────────
    private DrawOfferSettings drawOfferSettings = new DrawOfferSettings();
    private bool pendingAiDrawOffer;       // AI 已提和，等待玩家回應
    private bool isDrawOfferProcessed;     // 本局中提和流程已完成（接受或拒絕）
    private int lastAiDrawOfferMoveCount;  // AI 上次提和時的步數（冷卻用）
    private bool inCooldown;               // 是否在提和冷卻期中

    public bool IsDrawOfferProcessed => isDrawOfferProcessed;
    public bool IsOpeningBookLoaded => aiEngine.IsOpeningBookLoaded;
    public int OpeningBookEntryCount => aiEngine.OpeningBookEntryCount;

    // ─── 棋鐘（限時模式） ──────────────────────────────────────────────────
    /// <summary>
    /// 棋鐘實例。僅限時模式啟動時有值，非限時模式下為 null。
    /// </summary>
    public IGameClock? Clock { get; private set; }

    public bool IsTimedModeEnabled { get; set; } = false;
    public int TimedModeMinutesPerPlayer { get; set; } = 10;

    /// <summary>棋鐘 Tick 計時器：每秒觸發一次，驅動超時偵測。</summary>
    private System.Threading.Timer? clockTickTimer;

    public event Action? BoardUpdated;
    public event Action<string>? GameMessage;
    public event Action<SearchResult>? HintReady;
    public event Action<SearchResult>? HintUpdated;
    public event Action<string>? ThinkingProgress;
    public event Action<IReadOnlyList<MoveEvaluation>>? SmartHintReady;
    public event Action<IReadOnlyList<MoveEvaluation>>? MultiPvHintReady;
    public event Action<DrawOfferResult>? DrawOffered;
    public event Action<DrawOfferResult>? DrawOfferResolved;
    public event Action<MoveCompletedEventArgs>? MoveCompleted;

    private readonly IEngineProvider? engineProvider;

    public GameService(
        IAiEngine aiEngine,
        IHintExplanationService? hintExplanationService = null,
        IEngineProvider? engineProvider = null)
    {
        this.aiEngine = aiEngine;
        this.hintExplanationService = hintExplanationService;
        this.engineProvider = engineProvider;
        bookmarkManager = new BookmarkManager();
        board = new Board(); // 初始局面
    }

    public async Task StartGameAsync(GameMode mode)
    {
        currentMode = mode;
        aiCts?.Cancel();
        aiPauseSignal.Set();
        // 等待舊的 AI 搜尋任務完全結束後再重置棋盤，避免新舊任務並存的競爭條件
        // 最多等待 5 秒（500 × 10ms），防止異常情況下的無限等待
        var waitCount = 0;
        while (isThinking && waitCount < 500)
        {
            await Task.Delay(10);
            waitCount++;
        }
        board = new Board(); // 重置為標準初始局
        ClearLatestHint();
        isGameOver = false;
        pendingAiDrawOffer = false;
        isDrawOfferProcessed = false;
        lastAiDrawOfferMoveCount = 0;
        inCooldown = false;
        Interlocked.Exchange(ref completedGameNodes, 0);
        Interlocked.Exchange(ref lastSearchNodes, 0);
        Interlocked.Exchange(ref lastSearchNps, 0);
        board.ParseFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        initialFen = board.ToFen();
        moveHistory.Clear();
        replayState = ReplayState.Live;
        replayCurrentStep = 0;
        ResetWxfHistory();
        MoveHistoryChanged?.Invoke();
        ReplayStateChanged?.Invoke();

        // AiVsAi：依設定初始化黑方引擎
        if (currentMode == GameMode.AiVsAi)
        {
            if (engineProvider != null)
            {
                // engineProvider 管理引擎生命週期，GameService 不需自行克隆
                aiEngineBlack = null;
            }
            else
            {
                aiEngineBlack = UseSharedTranspositionTable
                    ? null                                     // Shared: reuse red engine
                    : (CopyRedTtToBlackAtStart
                        ? aiEngine.CloneWithCopiedTT()          // Independent: initialize with red TT snapshot
                        : aiEngine.CloneWithEmptyTT());         // Independent: initialize with empty TT
            }
        }
        else
        {
            aiEngineBlack = null;
        }

        // 棋鐘：限時模式才建立，非限時模式清除舊鐘
        StopAndDisposeClock();
        if (IsTimedModeEnabled)
        {
            var newClock = new GameClock(TimeSpan.FromMinutes(TimedModeMinutesPerPlayer));
            newClock.OnTimeout += OnClockTimeout;
            Clock = newClock;
            Clock.Start(board.Turn);
            // 每秒 Tick 一次以偵測超時
            clockTickTimer = new System.Threading.Timer(
                _ => Clock?.Tick(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
        }

        NotifyUpdate();
        GameMessage?.Invoke("Game Started");

        if (currentMode == GameMode.AiVsAi)
        {
            await RunAiSearchAsync(applyBestMove: true);
        }
        else if (currentMode == GameMode.PlayerVsAi && PlayerColor == PieceColor.Black)
        {
            // 玩家選黑方，AI（紅方）先手
            await RunAiSearchAsync(applyBestMove: true);
        }
    }

    public Task StopGameAsync()
    {
        aiPauseSignal.Set();
        aiCts?.Cancel();
        StopAndDisposeClock();
        ThinkingProgress?.Invoke("AI 思考已停止");
        return Task.CompletedTask;
    }

    public Task PauseThinkingAsync()
    {
        if (!isThinking)
        {
            ThinkingProgress?.Invoke("AI 目前未在思考中");
            return Task.CompletedTask;
        }

        aiPauseSignal.Reset();
        Clock?.Pause();
        return Task.CompletedTask;
    }

    public Task ResumeThinkingAsync()
    {
        if (!isThinking)
        {
            ThinkingProgress?.Invoke("AI 目前未在思考中");
            return Task.CompletedTask;
        }

        aiPauseSignal.Set();
        Clock?.Resume();
        return Task.CompletedTask;
    }

    public async Task ExportTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            ThinkingProgress?.Invoke("TT 匯出失敗：未指定檔案路徑");
            return;
        }

        try
        {
            await EnsureAiStoppedForPersistenceAsync(ct);

            ThinkingProgress?.Invoke("TT 匯出中...");
            await using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await aiEngine.ExportTranspositionTableAsync(file, asJson, ct);
            ThinkingProgress?.Invoke("TT 匯出完成");
        }
        catch (OperationCanceledException)
        {
            ThinkingProgress?.Invoke("TT 匯出已取消");
        }
        catch (Exception ex)
        {
            ThinkingProgress?.Invoke($"TT 匯出失敗：{ex.Message}");
        }
    }

    public async Task ImportTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            ThinkingProgress?.Invoke("TT 匯入失敗：未指定檔案路徑");
            return;
        }

        if (!File.Exists(filePath))
        {
            ThinkingProgress?.Invoke("TT 匯入失敗：檔案不存在");
            return;
        }

        try
        {
            await EnsureAiStoppedForPersistenceAsync(ct);

            ThinkingProgress?.Invoke("TT 匯入中...");
            await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await aiEngine.ImportTranspositionTableAsync(file, asJson, ct);
            ThinkingProgress?.Invoke("TT 匯入完成");
        }
        catch (OperationCanceledException)
        {
            ThinkingProgress?.Invoke("TT 匯入已取消");
        }
        catch (Exception ex)
        {
            ThinkingProgress?.Invoke($"TT 匯入失敗：{ex.Message}");
        }
    }

    public async Task ExportBlackTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default)
    {
        if (aiEngineBlack == null)
        {
            ThinkingProgress?.Invoke("黑方 TT 不存在（非獨立 TT 模式）");
            return;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            ThinkingProgress?.Invoke("黑方 TT 匯出失敗：未指定檔案路徑");
            return;
        }

        try
        {
            await EnsureAiStoppedForPersistenceAsync(ct);
            ThinkingProgress?.Invoke("黑方 TT 匯出中...");
            await using var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await aiEngineBlack.ExportTranspositionTableAsync(file, asJson, ct);
            ThinkingProgress?.Invoke("黑方 TT 匯出完成");
        }
        catch (OperationCanceledException)
        {
            ThinkingProgress?.Invoke("黑方 TT 匯出已取消");
        }
        catch (Exception ex)
        {
            ThinkingProgress?.Invoke($"黑方 TT 匯出失敗：{ex.Message}");
        }
    }

    public async Task ImportBlackTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default)
    {
        if (aiEngineBlack == null)
        {
            ThinkingProgress?.Invoke("黑方 TT 不存在（非獨立 TT 模式）");
            return;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            ThinkingProgress?.Invoke("黑方 TT 匯入失敗：未指定檔案路徑");
            return;
        }

        if (!File.Exists(filePath))
        {
            ThinkingProgress?.Invoke("黑方 TT 匯入失敗：檔案不存在");
            return;
        }

        try
        {
            await EnsureAiStoppedForPersistenceAsync(ct);
            ThinkingProgress?.Invoke("黑方 TT 匯入中...");
            await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await aiEngineBlack.ImportTranspositionTableAsync(file, asJson, ct);
            ThinkingProgress?.Invoke("黑方 TT 匯入完成");
        }
        catch (OperationCanceledException)
        {
            ThinkingProgress?.Invoke("黑方 TT 匯入已取消");
        }
        catch (Exception ex)
        {
            ThinkingProgress?.Invoke($"黑方 TT 匯入失敗：{ex.Message}");
        }
    }

    public async Task HumanMoveAsync(Move move)
    {
        if (isThinking) return;
        if (isGameOver) return;
        if (replayState == ReplayState.Replaying) return;
        if (currentMode == GameMode.AiVsAi) return;
        if (move.From >= Board.BoardSize || move.To >= Board.BoardSize || move.From == move.To)
        {
            GameMessage?.Invoke("非法落子：走法資料不完整");
            return;
        }

        var legalMoves = board.GenerateLegalMoves();
        if (!legalMoves.Contains(move))
        {
            GameMessage?.Invoke("非法落子：這不是合法著法");
            return;
        }

        var movedPiece    = board.GetPiece(move.From);
        var capturedPiece = board.GetPiece(move.To);
        var notation      = MoveNotation.ToNotation(move, board); // 必須在 MakeMove 前取得
        board.MakeMove(move);
        var wxfCls = MoveClassifier.Classify(board, move, movedPiece, capturedPiece, out int wxfVictimSq);
        wxfHistory.Add(new MoveRecord
        {
            ZobristKey     = board.ZobristKey,
            Turn           = movedPiece.Color,
            Move           = move,
            Classification = wxfCls,
            VictimSquare   = wxfVictimSq,
            IsCapture      = !capturedPiece.IsNone,
        });
        moveHistory.Add(new MoveHistoryEntry
        {
            StepNumber = moveHistory.Count + 1,
            Move       = move,
            Notation   = notation,
            Turn       = movedPiece.Color,
            IsCapture  = !capturedPiece.IsNone,
        });
        replayCurrentStep = moveHistory.Count;
        Clock?.SwitchTurn();
        ClearLatestHint();
        MoveHistoryChanged?.Invoke();
        NotifyUpdate();

        if (CheckGameOver()) return;

        if (currentMode == GameMode.PlayerVsAi)
        {
            await RunAiSearchAsync(applyBestMove: true);
        }
    }

    private async Task<SearchResult?> RunAiSearchAsync(bool applyBestMove)
    {
        // 重播模式下禁止套用走法
        if (applyBestMove && replayState == ReplayState.Replaying) return null;

        // 原子地將 isThinkingFlag 從 0 設為 1；若已是 1 表示另一搜尋進行中，直接返回
        if (Interlocked.CompareExchange(ref isThinkingFlag, 1, 0) != 0) return null;
        ThinkingProgress?.Invoke("AI 思考中...");

        // 提示搜尋開始時設旗標
        if (!applyBestMove)
        {
            isHintSearchingFlag = true;
        }

        var cts = new CancellationTokenSource();
        aiCts = cts;
        var boardSnapshot = board.Clone();
        SearchResult? result = null;
        var continueAiLoop = false;

        // 依目前輪次選擇對應的引擎與設定
        var activeEngine   = GetCurrentEngine();
        var activeSettings = GetCurrentSettings();
        var settings = new SearchSettings
        {
            Depth             = activeSettings.Depth,
            TimeLimitMs       = activeSettings.TimeLimitMs,
            ThreadCount       = activeSettings.ThreadCount,
            PauseSignal       = aiPauseSignal,
            AllowOpeningBook  = applyBestMove   // hint 模式（applyBestMove=false）不查開局庫
        };

        // 限時模式：純粹根據棋鐘剩餘時間計算 Soft/Hard 時限，不受固定時限設定影響
        // Soft 觸發：完成整層後停止（不中途截斷），保證回傳有效最佳著法
        // Hard 觸發：強制中途取消，防止超時
        if (applyBestMove && IsTimedModeEnabled && Clock != null)
        {
            var remaining = board.Turn == PieceColor.Red ? Clock.RedRemaining : Clock.BlackRemaining;
            var remainingMs = Math.Max(0, (int)remaining.TotalMilliseconds);
            var (softMs, hardMs) = CalculateTimedModeBudget(remainingMs);
            settings.SoftTimeLimitMs = softMs;
            settings.HardTimeLimitMs = hardMs;
        }

        long baseNodes = Interlocked.Read(ref completedGameNodes);
        long lastElapsedMs = 0;
        var progress = new Progress<SearchProgress>(p =>
        {
            Interlocked.Exchange(ref lastSearchNodes, baseNodes + p.Nodes);
            Interlocked.Exchange(ref lastSearchNps, p.NodesPerSecond);
            lastElapsedMs = p.ElapsedMs;
            ThinkingProgress?.Invoke(FormatThinkingProgress(p));

            // 提示搜尋模式下，非心跳且有有效座標且深度 >= 2 時觸發 HintUpdated
            if (!applyBestMove && !p.IsHeartbeat && p.BestMoveFrom >= 0 && p.CurrentDepth >= 2)
            {
                var hintResult = new SearchResult
                {
                    BestMove = new Domain.Entities.Move(p.BestMoveFrom, p.BestMoveTo),
                    Score = p.Score,
                    Depth = p.CurrentDepth,
                    Nodes = p.Nodes
                };
                HintUpdated?.Invoke(hintResult);
            }
        });

        try
        {
            result = await activeEngine.SearchAsync(
                boardSnapshot,
                settings,
                cts.Token,
                progress);

            if (cts.Token.IsCancellationRequested) return null;

            // 開局庫命中時發出提示訊息（IsFromOpeningBook 由 Decorator 設定）
            if (result.IsFromOpeningBook)
            {
                var bookNotation = MoveNotation.ToNotation(result.BestMove, board);
                ThinkingProgress?.Invoke($"開局庫選手：{bookNotation}");
            }

            if (result.BestMove.IsNull)
            {
                ThinkingProgress?.Invoke("AI 思考完成：目前無可用著法");
                if (applyBestMove)
                {
                    GameMessage?.Invoke("AI Resigns (No moves)");
                }
            }
            else
            {
                if (applyBestMove)
                {
                    var searchTurn     = board.Turn;
                    var moveNotation   = MoveNotation.ToNotation(result.BestMove, board);
                    var aiMovedPiece   = board.GetPiece(result.BestMove.From);
                    var aiCaptured     = board.GetPiece(result.BestMove.To);
                    board.MakeMove(result.BestMove);
                    var aiWxfCls = MoveClassifier.Classify(board, result.BestMove, aiMovedPiece, aiCaptured, out int aiVictimSq);
                    wxfHistory.Add(new MoveRecord
                    {
                        ZobristKey     = board.ZobristKey,
                        Turn           = aiMovedPiece.Color,
                        Move           = result.BestMove,
                        Classification = aiWxfCls,
                        VictimSquare   = aiVictimSq,
                        IsCapture      = !aiCaptured.IsNone,
                    });
                    moveHistory.Add(new MoveHistoryEntry
                    {
                        StepNumber = moveHistory.Count + 1,
                        Move       = result.BestMove,
                        Notation   = moveNotation,
                        Turn       = aiMovedPiece.Color,
                        IsCapture  = !aiCaptured.IsNone,
                    });
                    replayCurrentStep = moveHistory.Count;
                    Clock?.SwitchTurn();
                    ClearLatestHint();
                    MoveHistoryChanged?.Invoke();
                    NotifyUpdate();
                    var moveSource = result.IsFromOpeningBook ? "開局庫" : "AI";
                    var scoreStr = result.IsFromOpeningBook ? "定式" : FormatScore(result.Score, searchTurn == PieceColor.Red ? "紅方" : "黑方");
                    GameMessage?.Invoke($"{moveSource} 走了 {moveNotation}（{scoreStr}）");
                    ThinkingProgress?.Invoke(FormatHintProgress(result, searchTurn, "思考完成", moveNotation, lastElapsedMs));
                    MoveCompleted?.Invoke(new MoveCompletedEventArgs
                    {
                        Fen          = board.ToFen(),
                        MoveNotation = moveNotation,
                        Score        = result.Score,
                        Depth        = result.Depth,
                        Nodes        = result.Nodes,
                        PvLine       = result.PvLine,
                        MovedBy      = searchTurn,
                        MoveNumber   = board.MoveCount
                    });
                }
                else
                {
                    var moveNotation = MoveNotation.ToNotation(result.BestMove, board);
                    StoreLatestHint(result, board);
                    // HintReady 觸發前先清除搜尋旗標
                    isHintSearchingFlag = false;
                    HintReady?.Invoke(result);
                    ThinkingProgress?.Invoke(FormatHintProgress(result, board.Turn, "提示完成", moveNotation, lastElapsedMs));
                }

                if (applyBestMove && !CheckGameOver())
                {
                    // PlayerVsAi：AI 走完後檢查是否主動提和
                    if (currentMode == GameMode.PlayerVsAi && !isGameOver && !pendingAiDrawOffer)
                    {
                        TryAiDrawOffer(result.Score);
                        // 若 AI 提和，等待玩家回應，暫不繼續
                    }

                    if (currentMode == GameMode.AiVsAi)
                    {
                        // 繼續執行下一輪
                        continueAiLoop = true;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            ThinkingProgress?.Invoke("AI 思考已取消");
            if (applyBestMove)
            {
                GameMessage?.Invoke("AI Search Canceled");
            }
        }
        finally
        {
            Interlocked.Exchange(ref isThinkingFlag, 0);
            // 確保提示搜尋旗標在任何情況下都會清除（含異常路徑）
            if (!applyBestMove)
            {
                isHintSearchingFlag = false;
            }
            if (result != null)
            {
                Interlocked.Add(ref completedGameNodes, result.Nodes);
                Interlocked.Exchange(ref lastSearchNodes, Interlocked.Read(ref completedGameNodes));
            }
            if (applyBestMove)
            {
                NotifyUpdate();
            }
            // 先清除 field 再 dispose，防止外部在 dispose 後呼叫 Cancel 拋出 ObjectDisposedException
            if (ReferenceEquals(aiCts, cts)) aiCts = null;
            cts.Dispose();
        }

        if (continueAiLoop)
        {
            // 使用 ContinueWith 觀察例外，避免 fire-and-forget 靜默吞掉錯誤
            // CS4014 在此為刻意的非同步接續，不需要 await
#pragma warning disable CS4014
            RunAiSearchAsync(applyBestMove: true)
                .ContinueWith(
                    t => GameMessage?.Invoke($"AiVsAi 搜尋發生錯誤：{t.Exception?.InnerException?.Message ?? t.Exception?.Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
#pragma warning restore CS4014
        }

        return result;
    }

    public async Task<string> ExplainLatestHintAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (latestHint is null || latestHint.BestMove.IsNull)
        {
            return "目前沒有可用提示，請先按提示走法。";
        }

        if (latestHintFen is null || latestHintMoveCount != board.MoveCount || latestHintFen != board.ToFen())
        {
            return "目前局面已更新，請先重新取得提示，才能解釋這一步。";
        }

        if (hintExplanationService is null)
        {
            return "尚未設定提示解釋服務，請先補齊設定檔。";
        }

        // 若此次提示來自 MultiPV 且局面未變，帶入候選走法比較資訊
        IReadOnlyList<AlternativeMoveInfo>? alternatives = null;
        if (latestMultiPvEvaluations != null)
        {
            alternatives = latestMultiPvEvaluations
                .Where(e => !e.IsBest)
                .Select((e, i) => new AlternativeMoveInfo(
                    i + 2,
                    MoveNotation.ToNotation(e.Move, board),
                    e.Score,
                    e.PvLine))
                .ToList();
        }

        var request = new HintExplanationRequest
        {
            Fen = latestHintFen,
            SideToMove = board.Turn,
            BestMoveNotation = MoveNotation.ToNotation(latestHint.BestMove, board),
            Score = latestHint.Score,
            SearchDepth = latestHint.Depth,
            Nodes = latestHint.Nodes,
            PrincipalVariation = latestHint.PvLine,
            ThinkingTree = BuildThinkingTreeForPrompt(board.Clone()),
            AlternativeMoves = alternatives
        };

        try
        {
            return await hintExplanationService.ExplainAsync(request, progress, ct);
        }
        catch (OperationCanceledException)
        {
            return "解釋請求已取消。";
        }
        catch (Exception ex)
        {
            return $"解釋失敗：{ex.Message}";
        }
    }

    private string BuildThinkingTreeForPrompt(IBoard sourceBoard)
    {
        try
        {
            var engineForTree = GetCurrentEngine();
            var root = engineForTree.ExploreTTTree(sourceBoard, HintExplanationThinkingTreeDepth);
            if (root is null)
            {
                return "（當前局面未命中 TT，暫無可用思路樹）";
            }

            var sb = new StringBuilder();
            sb.AppendLine("【思路樹】");
            AppendThinkingTreeNode(sb, root, string.Empty, true, sourceBoard, HintExplanationThinkingTreeDepth);
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            return $"（取得思路樹失敗：{ex.Message}）";
        }
    }

    private static void AppendThinkingTreeNode(
        StringBuilder sb,
        TTTreeNode node,
        string indent,
        bool isRoot,
        IBoard parentBoard,
        int remainingDepth)
    {
        if (remainingDepth <= 0)
        {
            return;
        }

        var entry = node.Entry;
        string scoreText = entry.Score > 0 ? $"+{entry.Score}" : entry.Score.ToString();
        string flagText = entry.Flag switch
        {
            TTFlag.Exact => "=",
            TTFlag.LowerBound => "≥",
            TTFlag.UpperBound => "≤",
            _ => "?"
        };

        string moveText;
        IBoard boardAtNode;
        if (isRoot)
        {
            moveText = "（當前局面）";
            boardAtNode = parentBoard;
        }
        else
        {
            try
            {
                moveText = MoveNotation.ToNotation(node.MoveToHere, parentBoard);
            }
            catch
            {
                moveText = "（無法轉換該著法）";
            }

            boardAtNode = parentBoard.Clone();
            try
            {
                boardAtNode.MakeMove(node.MoveToHere);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
            {
                // 過期或碰撞的 TT 條目，走法在當前局面非法，維持 boardAtNode 不變
            }
        }

        sb.AppendLine($"{indent}{moveText} [{flagText} {scoreText}, depth:{entry.Depth}]");

        if (remainingDepth <= 1)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            var childBoard = boardAtNode.Clone();
            AppendThinkingTreeNode(sb, child, indent + "  ", isRoot: false, childBoard, remainingDepth - 1);
        }
    }

    // 根據目前輪次選擇引擎（AiVsAi 獨立TT 時，黑方用 aiEngineBlack）
    private IAiEngine GetCurrentEngine()
    {
        // 若有 engineProvider，依目前輪次顏色選擇對應引擎
        // 不限 AiVsAi 模式：PlayerVsAi 中 AI 以黑方出手時同樣需要黑方引擎
        if (engineProvider != null)
        {
            return board.Turn == PieceColor.Black
                ? engineProvider.GetBlackEngine()
                : engineProvider.GetRedEngine();
        }
        // 原邏輯（向下相容，無 engineProvider 時）
        if (currentMode == GameMode.AiVsAi && aiEngineBlack != null && board.Turn == PieceColor.Black)
            return aiEngineBlack;
        return aiEngine;
    }

    // 根據目前輪次選擇設定
    private SearchSettings GetCurrentSettings()
    {
        if (currentMode == GameMode.AiVsAi && board.Turn == PieceColor.Black)
            return blackAiSettings;
        return redAiSettings;
    }

    private bool isGameOver;

    // ─── 提和邏輯 ──────────────────────────────────────────────────────────

    /// <summary>玩家主動提和。僅在 PlayerVsAi 且遊戲進行中時有效。</summary>
    public async Task RequestDrawAsync()
    {
        if (currentMode != GameMode.PlayerVsAi) return;
        if (isGameOver) return;
        if (isThinking) return;

        // 開局階段拒絕提和（步數未達門檻）
        if (board.MoveCount < drawOfferSettings.MinMoveCountForAiDrawOffer)
        {
            string earlyReason = $"開局階段（走步：{board.MoveCount}），AI 拒絕提和";
            var earlyResult = new DrawOfferResult(DrawOfferSource.Player, false, earlyReason);
            isDrawOfferProcessed = true;
            GameMessage?.Invoke($"AI 拒絕提和（{earlyReason}）");
            DrawOfferResolved?.Invoke(earlyResult);
            return;
        }

        // 執行快速 AI 搜尋評估局面
        var boardSnapshot = board.Clone();
        var evalSettings = new SearchSettings
        {
            Depth = 2,
            TimeLimitMs = 1000,
            ThreadCount = 1,
            PauseSignal = aiPauseSignal,
            AllowOpeningBook = false  // 提和評估必須用真實搜尋分數，開局庫回傳 Score=0 會造成 AI 無條件接受提和
        };

        int score;
        try
        {
            var evalResult = await GetCurrentEngine().SearchAsync(boardSnapshot, evalSettings, CancellationToken.None);
            // 從黑方（AI）視角評估：正分表示黑方佔優
            // SearchAsync 回傳的分數是從搜尋方角度，黑方回合則取負值轉換為黑方優勢
            score = board.Turn == Domain.Enums.PieceColor.Black ? evalResult.Score : -evalResult.Score;
        }
        catch
        {
            score = 0;
        }

        // AI 分數 > DrawRefuseThreshold 時（AI 佔優），拒絕提和
        bool aiAccepts = Math.Abs(score) <= drawOfferSettings.DrawRefuseThreshold;
        string reason = aiAccepts
            ? $"AI 接受提和（分數：{score}，接近均勢）"
            : $"AI 拒絕提和（分數：{score}，AI 佔優）";

        var result = new DrawOfferResult(DrawOfferSource.Player, aiAccepts, reason);
        isDrawOfferProcessed = true;

        if (aiAccepts)
        {
            isGameOver = true;
            GameMessage?.Invoke($"和棋！玩家提和，AI 接受（{reason}）");
        }
        else
        {
            GameMessage?.Invoke($"AI 拒絕提和（{reason}）");
        }

        DrawOfferResolved?.Invoke(result);
    }

    /// <summary>回應 AI 的提和請求。</summary>
    public void RespondToDrawOffer(bool accept)
    {
        if (!pendingAiDrawOffer) return;

        pendingAiDrawOffer = false;
        isDrawOfferProcessed = true;

        var result = new DrawOfferResult(DrawOfferSource.Ai, accept,
            accept ? "玩家接受 AI 提和" : "玩家拒絕 AI 提和");

        if (accept)
        {
            isGameOver = true;
            GameMessage?.Invoke("和棋！AI 提和，玩家接受");
        }
        else
        {
            // 拒絕後啟動冷卻
            inCooldown = true;
            lastAiDrawOfferMoveCount = board.MoveCount;
            GameMessage?.Invoke("玩家拒絕 AI 提和，繼續對弈");
        }

        DrawOfferResolved?.Invoke(result);
    }

    /// <summary>（測試用）模擬 AI 已提和，設定待回應狀態。</summary>
    public void SimulateAiDrawOffer()
    {
        if (currentMode != GameMode.PlayerVsAi) return;
        if (isGameOver) return;
        if (inCooldown) return;

        pendingAiDrawOffer = true;
        var offerResult = new DrawOfferResult(DrawOfferSource.Ai, Accepted: false, "AI 提和（等待玩家回應）");
        DrawOffered?.Invoke(offerResult);
    }

    /// <summary>AI 搜尋完成後評估是否主動提和。</summary>
    private void TryAiDrawOffer(int searchScore)
    {
        if (currentMode != GameMode.PlayerVsAi) return;
        if (isGameOver) return;
        if (pendingAiDrawOffer) return;

        // 冷卻期檢查
        if (inCooldown && (board.MoveCount - lastAiDrawOfferMoveCount) < drawOfferSettings.CooldownMoves)
            return;

        inCooldown = false;

        // 步數門檻
        if (board.MoveCount < drawOfferSettings.MinMoveCountForAiDrawOffer) return;

        // 均勢門檻：分數絕對值在閾值內
        if (Math.Abs(searchScore) > drawOfferSettings.DrawOfferThreshold) return;

        // 觸發 AI 主動提和
        pendingAiDrawOffer = true;
        var offerResult = new DrawOfferResult(
            DrawOfferSource.Ai,
            Accepted: false,
            $"AI 提和：局面均勢（分數：{searchScore}）");
        GameMessage?.Invoke($"AI 主動提和（分數：{searchScore}，走步：{board.MoveCount}）");
        DrawOffered?.Invoke(offerResult);
    }

    private bool CheckGameOver()
    {
        // WXF 重複局面裁決（取代簡易三次重複和棋）
        // 種子條目（1）+ 至少兩個完整循環（8步）= 9 筆才可能觸發
        if (wxfHistory.Count >= 9)
        {
            var verdict = WxfRepetitionJudge.Judge(wxfHistory);
            if (verdict != RepetitionVerdict.None)
            {
                isGameOver = true;
                StopAndDisposeClock();
                switch (verdict)
                {
                    case RepetitionVerdict.Draw:
                        GameMessage?.Invoke("和棋！重複局面（WXF）");
                        break;
                    case RepetitionVerdict.RedWins:
                        GameMessage?.Invoke("紅方勝！黑方長將/長捉犯規");
                        break;
                    case RepetitionVerdict.BlackWins:
                        GameMessage?.Invoke("黑方勝！紅方長將/長捉犯規");
                        break;
                }
                return true;
            }
        }

        // 和棋判定（六十步無吃子）
        if (board.IsDrawByNoCapture())
        {
            isGameOver = true;
            StopAndDisposeClock();
            GameMessage?.Invoke("和棋！六十步無吃子");
            return true;
        }

        var currentTurn = board.Turn;
        if (board.GenerateLegalMoves().Any()) return false;

        isGameOver = true;
        StopAndDisposeClock();
        var winner = currentTurn == PieceColor.Red ? "黑方" : "紅方";
        if (board.IsCheck(currentTurn))
            GameMessage?.Invoke($"將死！{winner}獲勝！");
        else
            GameMessage?.Invoke($"困斃！{winner}獲勝！"); // 中國象棋中困斃也算輸

        return true;
    }

    public void Undo()
    {
        if (isThinking) return;
        if (replayState == ReplayState.Replaying) return;

        bool didUndo = false;

        void TryUndo()
        {
            if (!board.TryGetLastMove(out _))
            {
                GameMessage?.Invoke("無可悔棋步數");
                return;
            }

            try
            {
                board.UndoMove();
                didUndo = true;
                // 移除最後一筆 wxfHistory（保留種子條目）
                if (wxfHistory.Count > 1)
                    wxfHistory.RemoveAt(wxfHistory.Count - 1);
                // 移除最後一筆 moveHistory
                if (moveHistory.Count > 0)
                    moveHistory.RemoveAt(moveHistory.Count - 1);
                replayCurrentStep = moveHistory.Count;
            }
            catch (InvalidOperationException)
            {
                GameMessage?.Invoke("悔棋失敗");
            }
        }

        TryUndo();
        if (currentMode == GameMode.PlayerVsAi)
        {
            if (board.TryGetLastMove(out _))
            {
                TryUndo();
            }
            else if (didUndo)
            {
                GameMessage?.Invoke("無可悔棋步數");
            }
        }

        if (didUndo)
        {
            // 悔棋後允許繼續走棋，重設遊戲結束旗標
            isGameOver = false;
            MoveHistoryChanged?.Invoke();
            NotifyUpdate();
        }
    }

    public void Redo()
    {
        // Minimal no-op implementation to avoid hard crash.
        GameMessage?.Invoke("Redo 功能未實作");
    }

    public void AddBookmark(string name) => bookmarkManager.AddBookmark(name, board.ToFen());
    public void LoadBookmark(string name)
    {
        var fen = bookmarkManager.GetBookmark(name);
        if (fen != null)
        {
            board.ParseFen(fen);
            ResetWxfHistory();
            // 重設遊戲狀態，避免遊戲結束後載入書籤仍無法繼續走棋
            isGameOver = false;
            pendingAiDrawOffer = false;
            isDrawOfferProcessed = false;
            ClearLatestHint();
            NotifyUpdate();
        }
    }
    public void DeleteBookmark(string name) => bookmarkManager.DeleteBookmark(name);
    public IEnumerable<string> GetBookmarks() => bookmarkManager.GetBookmarkNames();

    public void SetDrawOfferSettings(DrawOfferSettings settings) => drawOfferSettings = settings;

    public void SetDifficulty(int depth, int timeMs, int threadCount = 0)
    {
        redAiSettings.Depth      = depth;
        redAiSettings.TimeLimitMs = timeMs;
        blackAiSettings.Depth     = depth;
        blackAiSettings.TimeLimitMs = timeMs;
        if (threadCount > 0)
        {
            redAiSettings.ThreadCount   = threadCount;
            blackAiSettings.ThreadCount = threadCount;
        }
    }

    public void SetRedAiDifficulty(int depth, int timeMs, int threadCount = 0)
    {
        redAiSettings.Depth      = depth;
        redAiSettings.TimeLimitMs = timeMs;
        if (threadCount > 0) redAiSettings.ThreadCount = threadCount;
    }

    public void SetBlackAiDifficulty(int depth, int timeMs, int threadCount = 0)
    {
        blackAiSettings.Depth      = depth;
        blackAiSettings.TimeLimitMs = timeMs;
        if (threadCount > 0) blackAiSettings.ThreadCount = threadCount;
    }

    public async Task<SearchResult> GetHintAsync()
    {
        if (IsMultiPvHintEnabled)
            return await GetMultiPvHintInternalAsync();

        var result = await RunAiSearchAsync(applyBestMove: false);
        if (result == null || result.BestMove.IsNull)
        {
            ClearLatestHint();
            return new SearchResult { BestMove = Move.Null };
        }

        // StoreLatestHint 已在 RunAiSearchAsync 的 else 分支呼叫，此處無需重複呼叫
        return result;
    }

    private async Task<SearchResult> GetMultiPvHintInternalAsync()
    {
        if (Interlocked.CompareExchange(ref isThinkingFlag, 1, 0) != 0)
            return new SearchResult { BestMove = Move.Null };

        isHintSearchingFlag = true;
        var cts = new CancellationTokenSource();
        var boardSnapshot = board.Clone();
        var activeEngine = GetCurrentEngine();
        var activeSettings = GetCurrentSettings();

        // 套用 UI 設定的思考時間限制，與普通 AI 搜尋的時間控制語意一致
        if (activeSettings.TimeLimitMs > 0)
            cts.CancelAfter(activeSettings.TimeLimitMs);

        aiCts = cts;
        var settings = new SearchSettings
        {
            Depth = activeSettings.Depth,
            TimeLimitMs = activeSettings.TimeLimitMs,
            ThreadCount = activeSettings.ThreadCount,
            PauseSignal = aiPauseSignal,
            AllowOpeningBook = false
        };

        try
        {
            ThinkingProgress?.Invoke("MultiPV 提示搜尋中...");

            var evaluations = await activeEngine.SearchMultiPvAsync(
                boardSnapshot, settings, MultiPvCount, cts.Token);

            var best = evaluations.FirstOrDefault(e => e.IsBest) ?? evaluations.FirstOrDefault();
            var result = new SearchResult
            {
                BestMove = best?.Move ?? Move.Null,
                Score = best?.Score ?? 0,
                Depth = settings.Depth,
                PvLine = best?.PvLine ?? string.Empty
            };

            StoreLatestHint(result, board);
            latestMultiPvEvaluations = evaluations;
            isHintSearchingFlag = false;
            HintReady?.Invoke(result);
            MultiPvHintReady?.Invoke(evaluations);

            var notation = !result.BestMove.IsNull
                ? MoveNotation.ToNotation(result.BestMove, board) : "（無）";
            ThinkingProgress?.Invoke($"MultiPV 提示完成：最佳 {notation}，共 {evaluations.Count} 個著法");

            return result;
        }
        catch (OperationCanceledException)
        {
            ThinkingProgress?.Invoke("MultiPV 提示已取消");
            return new SearchResult { BestMove = Move.Null };
        }
        finally
        {
            Interlocked.Exchange(ref isThinkingFlag, 0);
            isHintSearchingFlag = false;
            if (ReferenceEquals(aiCts, cts)) aiCts = null;
            cts.Dispose();
        }
    }

    private void StoreLatestHint(SearchResult hint, IBoard sourceBoard)
    {
        if (hint.BestMove.IsNull)
        {
            ClearLatestHint();
            return;
        }

        latestHint = new SearchResult
        {
            BestMove = hint.BestMove,
            Score = hint.Score,
            Depth = hint.Depth,
            Nodes = hint.Nodes,
            PvLine = hint.PvLine
        };
        latestHintFen = sourceBoard.ToFen();
        latestHintMoveCount = sourceBoard.MoveCount;
    }

    private void ClearLatestHint()
    {
        latestHint = null;
        latestHintFen = null;
        latestHintMoveCount = -1;
        latestMultiPvEvaluations = null;
    }

    public TTStatistics GetTTStatistics() => aiEngine.GetTTStatistics();

    public TTStatistics? GetBlackTTStatistics()
    {
        if (aiEngineBlack == null) return null;
        return aiEngineBlack.GetTTStatistics();
    }

    public async Task MergeTranspositionTablesAsync(CancellationToken ct = default)
    {
        if (aiEngineBlack == null)
        {
            ThinkingProgress?.Invoke("合併 TT：兩個引擎共用同一個 TT，無需合併");
            return;
        }

        try
        {
            await EnsureAiStoppedForPersistenceAsync(ct);
            ThinkingProgress?.Invoke("合併兩方 TT 中...");
            // 雙向合併：讓兩方都取得最深的分析結果
            aiEngine.MergeTranspositionTableFrom(aiEngineBlack);
            aiEngineBlack.MergeTranspositionTableFrom(aiEngine);
            ThinkingProgress?.Invoke("TT 合併完成");
        }
        catch (OperationCanceledException)
        {
            ThinkingProgress?.Invoke("TT 合併已取消");
        }
        catch (Exception ex)
        {
            ThinkingProgress?.Invoke($"TT 合併失敗：{ex.Message}");
        }
    }

    public IEnumerable<TTEntry> EnumerateTTEntries() => aiEngine.EnumerateTTEntries();

    public TTTreeNode? ExploreTTTree(int maxDepth = 6) =>
        aiEngine.ExploreTTTree(CurrentBoard, maxDepth);

    public async Task RequestSmartHintAsync(int fromIndex, CancellationToken ct = default)
    {
        if (!IsSmartHintEnabled) return;

        // 取消並釋放上一次尚未完成的智能提示搜尋
        var prevSmartHintCts = smartHintCts;
        smartHintCts = new CancellationTokenSource();
        prevSmartHintCts?.Cancel();
        prevSmartHintCts?.Dispose();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, smartHintCts.Token);

        try
        {
            var boardSnapshot = board.Clone();
            var moves = boardSnapshot.GenerateLegalMoves()
                .Where(m => m.From == fromIndex)
                .ToList();

            if (moves.Count == 0) return;

            ThinkingProgress?.Invoke($"智能提示：開始分析 {moves.Count} 個走法（深度 {SmartHintDepth}）...");

            var smartHintStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<string>(msg => ThinkingProgress?.Invoke(msg));
            var evaluations = await aiEngine.EvaluateMovesAsync(boardSnapshot, moves, SmartHintDepth, linkedCts.Token, progress);
            smartHintStopwatch.Stop();

            if (!linkedCts.Token.IsCancellationRequested)
            {
                var best = evaluations.FirstOrDefault(e => e.IsBest);
                if (best != null)
                {
                    string scoreStr    = best.Score > 0 ? $"+{best.Score}" : best.Score.ToString();
                    string bestNotation = MoveNotation.ToNotation(best.Move, board);
                    string elapsedText = $" | 用時: {smartHintStopwatch.Elapsed.TotalSeconds:0.0}s";
                    ThinkingProgress?.Invoke($"智能提示完成：最佳走法 {bestNotation} | 分數 {scoreStr} | 共 {evaluations.Count} 個走法{elapsedText}");
                }
                SmartHintReady?.Invoke(evaluations);
            }
        }
        catch (OperationCanceledException)
        {
            // 已被新選棋取消，靜默處理
        }
    }

    private string FormatThinkingProgress(SearchProgress progress)
    {
        var elapsedSeconds = progress.ElapsedMs > 0
            ? $"{(progress.ElapsedMs / 1000.0):0.0}s"
            : "0.0s";
        var speed = progress.NodesPerSecond > 0
            ? $"{progress.NodesPerSecond:N0} nodes/s"
            : "n/a";
        var turnLabel = board.Turn == PieceColor.Red ? "紅方" : "黑方";
        var scoreText = FormatScore(progress.Score, turnLabel);
        var bestMove = string.IsNullOrWhiteSpace(progress.BestMove) ? "待更新" : progress.BestMove;
        var mode = progress.IsHeartbeat ? "（即時）" : "（階段）";

        var ttHitRate = progress.TtHitRate > 0
            ? $"，TT:{progress.TtHitRate:P0}"
            : string.Empty;
        return $"AI 思考中{mode}：深度 {progress.CurrentDepth}/{progress.MaxDepth}，耗時 {elapsedSeconds}，節點 {progress.Nodes}（{speed}），分數 {scoreText}，建議 {bestMove}{ttHitRate}";
    }

    private static string FormatHintProgress(SearchResult result, PieceColor searchTurn, string label, string? notation = null, long elapsedMs = 0)
    {
        if (result.BestMove.IsNull)
        {
            return $"{label}：目前局面沒有可行的最佳走法";
        }

        var turnLabel = searchTurn == PieceColor.Red ? "紅方" : "黑方";
        var scoreText = FormatScore(result.Score, turnLabel);
        var moveText  = notation ?? result.BestMove.ToString();
        var elapsedText = elapsedMs > 0 ? $" | 用時: {elapsedMs / 1000.0:0.0}s" : string.Empty;

        return $"{label}：{moveText} | 分數: {scoreText} | 深度: {result.Depth} | 節點: {result.Nodes}{elapsedText}";
    }

    private static string FormatScore(int score, string turnLabel)
    {
        string signedScore = score switch
        {
            > 0 => $"+{score}",
            < 0 => score.ToString(),
            _ => "0"
        };

        if (Math.Abs(score) >= 15000)
        {
            return $"{signedScore}（{turnLabel}，高分）";
        }

        return $"{signedScore}（{turnLabel}）";
    }

    private void NotifyUpdate() => BoardUpdated?.Invoke();

    // ═══════════════════════════════════════════════════════════════════════════
    // 棋局歷史與重播方法
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>進入重播模式：等待 AI 完全停止後切換狀態。</summary>
    public async Task EnterReplayModeAsync()
    {
        if (replayState == ReplayState.Replaying) return;

        // 停止 AI 並等待完成
        aiCts?.Cancel();
        aiPauseSignal.Set();
        var waited = 0;
        while (isThinking && waited < 500)
        {
            await Task.Delay(10);
            waited++;
        }

        replayState = ReplayState.Replaying;
        // replayCurrentStep 維持當前步號（指向最新局面）
        ReplayStateChanged?.Invoke();
    }

    /// <summary>
    /// 跳躍至第 step 步後局面（0 = 初始局面）。
    /// 從頭重建棋盤與 wxfHistory。
    /// </summary>
    public async Task NavigateToAsync(int step)
    {
        if (replayState != ReplayState.Replaying) await EnterReplayModeAsync();

        step = Math.Clamp(step, 0, moveHistory.Count);

        // 建立全新棋盤，從初始 FEN 開始重播
        var replayBoard = new Board();
        replayBoard.ParseFen(initialFen);

        // 重建 wxfHistory（從頭開始）
        wxfHistory.Clear();
        wxfHistory.Add(new MoveRecord
        {
            ZobristKey     = replayBoard.ZobristKey,
            Turn           = replayBoard.Turn,
            Move           = Move.Null,
            Classification = MoveClassification.Cancel,
            VictimSquare   = -1,
            IsCapture      = false,
        });

        // 依序重播至目標步
        for (int i = 0; i < step; i++)
        {
            var entry        = moveHistory[i];
            var movedPiece   = replayBoard.GetPiece(entry.Move.From);
            var capturedPiece = replayBoard.GetPiece(entry.Move.To);
            replayBoard.MakeMove(entry.Move);
            var cls = MoveClassifier.Classify(replayBoard, entry.Move, movedPiece, capturedPiece, out int victimSq);
            wxfHistory.Add(new MoveRecord
            {
                ZobristKey     = replayBoard.ZobristKey,
                Turn           = movedPiece.Color,
                Move           = entry.Move,
                Classification = cls,
                VictimSquare   = victimSq,
                IsCapture      = !capturedPiece.IsNone,
            });
        }

        board = replayBoard;
        replayCurrentStep = step;
        // 到達最新步時恢復 Live 模式
        replayState = (step >= moveHistory.Count) ? ReplayState.Live : ReplayState.Replaying;

        ReplayStateChanged?.Invoke();
        NotifyUpdate();
    }

    public async Task StepForwardAsync()
    {
        if (replayState != ReplayState.Replaying) return;
        if (replayCurrentStep >= moveHistory.Count) return;
        await NavigateToAsync(replayCurrentStep + 1);
    }

    public async Task StepBackAsync()
    {
        if (replayState != ReplayState.Replaying) return;
        if (replayCurrentStep <= 0) return;
        await NavigateToAsync(replayCurrentStep - 1);
    }

    public async Task GoToStartAsync()
    {
        if (replayState != ReplayState.Replaying) return;
        await NavigateToAsync(0);
    }

    public async Task GoToEndAsync()
    {
        await NavigateToAsync(moveHistory.Count);
        // NavigateToAsync 在 step == Count 時會自動恢復 Live
    }

    /// <summary>
    /// 從目前重播局面繼續對弈（中途換手）。
    /// 截斷 MoveHistory 至目前步，切換模式為 mode。
    /// </summary>
    public async Task ContinueFromCurrentPositionAsync(GameMode mode)
    {
        if (replayState != ReplayState.Replaying) return;

        // 截斷歷史至目前步
        if (replayCurrentStep < moveHistory.Count)
            moveHistory.RemoveRange(replayCurrentStep, moveHistory.Count - replayCurrentStep);

        // 重設對局狀態
        currentMode = mode;
        isGameOver = false;
        pendingAiDrawOffer = false;
        isDrawOfferProcessed = false;
        Interlocked.Exchange(ref completedGameNodes, 0);
        ClearLatestHint();

        // 重建棋鐘（若限時模式）
        StopAndDisposeClock();
        if (IsTimedModeEnabled)
        {
            var newClock = new GameClock(TimeSpan.FromMinutes(TimedModeMinutesPerPlayer));
            newClock.OnTimeout += OnClockTimeout;
            Clock = newClock;
            Clock.Start(board.Turn);
            clockTickTimer = new System.Threading.Timer(
                _ => Clock?.Tick(),
                null,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
        }

        // 重建 wxfHistory（以當前棋盤狀態為新起點）
        ResetWxfHistory();

        // AiVsAi：依設定初始化黑方引擎
        if (mode == GameMode.AiVsAi && engineProvider == null)
        {
            aiEngineBlack = UseSharedTranspositionTable
                ? null
                : (CopyRedTtToBlackAtStart
                    ? aiEngine.CloneWithCopiedTT()
                    : aiEngine.CloneWithEmptyTT());
        }
        else if (mode != GameMode.AiVsAi)
        {
            aiEngineBlack = null;
        }

        replayState = ReplayState.Live;
        ReplayStateChanged?.Invoke();
        MoveHistoryChanged?.Invoke();
        NotifyUpdate();

        // 若新模式 AI 先手，啟動搜尋
        bool aiFirstMove = (mode == GameMode.AiVsAi) ||
                           (mode == GameMode.PlayerVsAi && PlayerColor != board.Turn);
        if (aiFirstMove)
        {
            await RunAiSearchAsync(applyBestMove: true);
        }
    }

    /// <summary>載入外部 GameRecord，進入重播模式。</summary>
    public async Task LoadGameRecordAsync(GameRecord record)
    {
        // 停止當前 AI
        aiCts?.Cancel();
        aiPauseSignal.Set();
        var waited = 0;
        while (isThinking && waited < 500)
        {
            await Task.Delay(10);
            waited++;
        }

        // 驗證 FEN
        var loadedBoard = new Board();
        loadedBoard.ParseFen(record.InitialFen);

        // 重建 moveHistory
        initialFen = record.InitialFen;
        moveHistory.Clear();
        foreach (var step in record.Steps)
        {
            moveHistory.Add(new MoveHistoryEntry
            {
                StepNumber = step.StepNumber,
                Move       = new Move(step.From, step.To),
                Notation   = step.Notation,
                Turn       = step.Turn == "Red" ? PieceColor.Red : PieceColor.Black,
                IsCapture  = step.IsCapture,
            });
        }

        isGameOver = false;
        pendingAiDrawOffer = false;
        isDrawOfferProcessed = false;
        ClearLatestHint();
        StopAndDisposeClock();

        // 進入重播模式，先設為 Replaying 以免 NavigateToAsync 進入 Live
        replayState = ReplayState.Replaying;
        replayCurrentStep = moveHistory.Count;

        // 跳至最後一步（同時重建棋盤）
        // 注意：NavigateToAsync 在 step == Count 時預設轉為 Live，此處需強制保持 Replaying
        await NavigateToAsync(moveHistory.Count);

        // NavigateToAsync 可能在最後一步設 Live，載入棋局時應保持 Replaying
        if (moveHistory.Count > 0)
        {
            replayState = ReplayState.Replaying;
        }

        MoveHistoryChanged?.Invoke();
        ReplayStateChanged?.Invoke();
    }

    /// <summary>將目前棋局匯出為 GameRecord。</summary>
    public GameRecord ExportGameRecord(string redPlayer = "玩家", string blackPlayer = "AI")
    {
        var steps = moveHistory.Select(e => new GameRecordStep
        {
            StepNumber = e.StepNumber,
            From       = (byte)e.Move.From,
            To         = (byte)e.Move.To,
            Notation   = e.Notation,
            Turn       = e.Turn == PieceColor.Red ? "Red" : "Black",
            IsCapture  = e.IsCapture,
        }).ToList();

        return new GameRecord
        {
            Metadata = new GameRecordMetadata
            {
                RedPlayer   = redPlayer,
                BlackPlayer = blackPlayer,
                Date        = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Result      = isGameOver ? "已結束" : "進行中",
                GameMode    = currentMode,
            },
            InitialFen = initialFen,
            Steps      = steps,
        };
    }

    /// <summary>
    /// 清空並重置 WXF 歷史，加入初始局面的種子條目。
    /// 種子條目使用 Cancel 分類，讓 WxfRepetitionJudge 能正確計算包含起始局面的重複次數。
    /// </summary>
    private void ResetWxfHistory()
    {
        wxfHistory.Clear();
        wxfHistory.Add(new MoveRecord
        {
            ZobristKey     = board.ZobristKey,
            Turn           = board.Turn,
            Move           = Move.Null,
            Classification = MoveClassification.Cancel,
            VictimSquare   = -1,
            IsCapture      = false,
        });
    }

    /// <summary>
    /// 超時事件處理：某方時間耗盡，判定對方獲勝。
    /// 由 System.Threading.Timer 執行緒觸發，注意 GameMessage 訂閱者須自行處理執行緒切換。
    /// </summary>
    private void OnClockTimeout(object? sender, PieceColor timedOutColor)
    {
        if (isGameOver) return;
        isGameOver = true;
        StopAndDisposeClock();
        var loser  = timedOutColor == PieceColor.Red ? "紅方" : "黑方";
        var winner = timedOutColor == PieceColor.Red ? "黑方" : "紅方";
        GameMessage?.Invoke($"時間到！{loser}超時，{winner}獲勝！");
    }

    /// <summary>停止並釋放棋鐘及 Tick 計時器。</summary>
    private void StopAndDisposeClock()
    {
        clockTickTimer?.Dispose();
        clockTickTimer = null;

        if (Clock != null)
        {
            Clock.OnTimeout -= OnClockTimeout;
            Clock.Stop();
            Clock = null;
        }
    }

    /// <summary>
    /// 根據棋鐘剩餘時間計算 Soft/Hard 搜尋時限。
    /// <para>
    /// 預估剩餘 <c>EstimatedMovesLeft</c> 步，每步分配 Soft = 剩餘時間 / 30，
    /// Hard = Soft × 2。不受固定時限設定影響，完全由棋鐘剩餘時間決定。
    /// 剩餘時間極少（≤ 500ms）時返回最低限度時限，確保仍能走棋。
    /// </para>
    /// </summary>
    internal static (int softMs, int hardMs) CalculateTimedModeBudget(int remainingMs)
    {
        const int EstimatedMovesLeft = 30;
        if (remainingMs > 500)
        {
            int softMs = Math.Max(100, remainingMs / EstimatedMovesLeft);
            int hardMs = softMs * 2;
            return (softMs, hardMs);
        }
        // 剩餘時間極少：快速走棋，避免直接超時
        return (100, 300);
    }

    public void Dispose()
    {
        StopAndDisposeClock();
        aiCts?.Cancel();
        aiCts?.Dispose();
        smartHintCts?.Cancel();
        smartHintCts?.Dispose();
        aiPauseSignal.Dispose();
    }

    private async Task EnsureAiStoppedForPersistenceAsync(CancellationToken ct)
    {
        if (!isThinking)
        {
            return;
        }

        aiPauseSignal.Set();
        aiCts?.Cancel();
        while (isThinking)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }
}
