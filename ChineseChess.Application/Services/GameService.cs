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
    private SearchSettings redAiSettings  = new SearchSettings();
    private SearchSettings blackAiSettings = new SearchSettings();
    private CancellationTokenSource? aiCts;
    private CancellationTokenSource? smartHintCts;
    private readonly ManualResetEventSlim aiPauseSignal = new ManualResetEventSlim(true);
    private readonly IHintExplanationService? hintExplanationService;
    private SearchResult? latestHint;
    private string? latestHintFen;
    private int latestHintMoveCount = -1;

    public IBoard CurrentBoard => board;
    public GameMode CurrentMode => currentMode;
    public bool IsThinking => isThinking; // 讀取 isThinkingFlag（透過私有 property）
    public Move? LastMove => board.TryGetLastMove(out var lastMove) ? lastMove : null;
    public bool IsSmartHintEnabled { get; set; } = true;
    public int SmartHintDepth { get; set; } = 2;
    public long LastSearchNodes => Interlocked.Read(ref lastSearchNodes);
    public long LastSearchNps => Interlocked.Read(ref lastSearchNps);
    public bool UseSharedTranspositionTable { get; set; } = false;
    private long lastSearchNodes;
    private long lastSearchNps;
    private long completedGameNodes; // 歷史已完成搜尋的累計節點數（本局）

    // ─── 提和相關欄位 ──────────────────────────────────────────────────────
    private DrawOfferSettings drawOfferSettings = new DrawOfferSettings();
    private bool pendingAiDrawOffer;       // AI 已提和，等待玩家回應
    private bool isDrawOfferProcessed;     // 本局中提和流程已完成（接受或拒絕）
    private int lastAiDrawOfferMoveCount;  // AI 上次提和時的步數（冷卻用）
    private bool inCooldown;               // 是否在提和冷卻期中

    public bool IsDrawOfferProcessed => isDrawOfferProcessed;

    public event Action? BoardUpdated;
    public event Action<string>? GameMessage;
    public event Action<SearchResult>? HintReady;
    public event Action<string>? ThinkingProgress;
    public event Action<IReadOnlyList<MoveEvaluation>>? SmartHintReady;
    public event Action<DrawOfferResult>? DrawOffered;
    public event Action<DrawOfferResult>? DrawOfferResolved;

    public GameService(IAiEngine aiEngine, IHintExplanationService? hintExplanationService = null)
    {
        this.aiEngine = aiEngine;
        this.hintExplanationService = hintExplanationService;
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

        // AiVsAi：依設定初始化黑方引擎
        if (currentMode == GameMode.AiVsAi)
        {
            aiEngineBlack = UseSharedTranspositionTable
                ? null                           // 共用：直接用 aiEngine
                : aiEngine.CloneWithCopiedTT(); // 獨立：從紅方 TT 複製一份
        }
        else
        {
            aiEngineBlack = null;
        }

        NotifyUpdate();
        GameMessage?.Invoke("Game Started");

        if (currentMode == GameMode.AiVsAi || (currentMode == GameMode.PlayerVsAi && board.Turn == PieceColor.Black))
        {
            // 若 AI 先手（例如自訂 FEN 或 AiVsAi），就觸發 AI 下棋。
            // 一般標準局面在 PvAI 下，紅方（Player）預設先行，除非有其他設定。
            if (currentMode == GameMode.AiVsAi)
            {
                await RunAiSearchAsync(applyBestMove: true);
            }
        }
    }

    public Task StopGameAsync()
    {
        aiPauseSignal.Set();
        aiCts?.Cancel();
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

        board.MakeMove(move);
        ClearLatestHint();
        NotifyUpdate();

        if (CheckGameOver()) return;

        if (currentMode == GameMode.PlayerVsAi)
        {
            await RunAiSearchAsync(applyBestMove: true);
        }
    }

    private async Task<SearchResult?> RunAiSearchAsync(bool applyBestMove)
    {
        // 原子地將 isThinkingFlag 從 0 設為 1；若已是 1 表示另一搜尋進行中，直接返回
        if (Interlocked.CompareExchange(ref isThinkingFlag, 1, 0) != 0) return null;
        ThinkingProgress?.Invoke("AI 思考中...");

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
            Depth       = activeSettings.Depth,
            TimeLimitMs = activeSettings.TimeLimitMs,
            ThreadCount = activeSettings.ThreadCount,
            PauseSignal = aiPauseSignal
        };
        long baseNodes = Interlocked.Read(ref completedGameNodes);
        var progress = new Progress<SearchProgress>(p =>
        {
            Interlocked.Exchange(ref lastSearchNodes, baseNodes + p.Nodes);
            Interlocked.Exchange(ref lastSearchNps, p.NodesPerSecond);
            ThinkingProgress?.Invoke(FormatThinkingProgress(p));
        });

        try
        {
            result = await activeEngine.SearchAsync(
                boardSnapshot,
                settings,
                cts.Token,
                progress);

            if (cts.Token.IsCancellationRequested) return null;

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
                    var searchTurn   = board.Turn;
                    var moveNotation = MoveNotation.ToNotation(result.BestMove, board);
                    board.MakeMove(result.BestMove);
                    ClearLatestHint();
                    NotifyUpdate();
                    GameMessage?.Invoke($"AI 走了 {moveNotation}（分數：{FormatScore(result.Score, searchTurn == PieceColor.Red ? "紅方" : "黑方")}）");
                    ThinkingProgress?.Invoke(FormatHintProgress(result, searchTurn, moveNotation));
                }
                else
                {
                    var moveNotation = MoveNotation.ToNotation(result.BestMove, board);
                    StoreLatestHint(result, board);
                    HintReady?.Invoke(result);
                    ThinkingProgress?.Invoke(FormatHintProgress(result, board.Turn, moveNotation));
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

        var request = new HintExplanationRequest
        {
            Fen = latestHintFen,
            SideToMove = board.Turn,
            BestMoveNotation = MoveNotation.ToNotation(latestHint.BestMove, board),
            Score = latestHint.Score,
            SearchDepth = latestHint.Depth,
            Nodes = latestHint.Nodes,
            PrincipalVariation = latestHint.PvLine,
            ThinkingTree = BuildThinkingTreeForPrompt(board.Clone())
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
            PauseSignal = aiPauseSignal
        };

        int score;
        try
        {
            var evalResult = await aiEngine.SearchAsync(boardSnapshot, evalSettings, CancellationToken.None);
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
        // 和棋優先判定（三次重覆局面）
        if (board.IsDrawByRepetition())
        {
            isGameOver = true;
            GameMessage?.Invoke("和棋！三次重覆局面");
            return true;
        }

        // 和棋判定（六十步無吃子）
        if (board.IsDrawByNoCapture())
        {
            isGameOver = true;
            GameMessage?.Invoke("和棋！六十步無吃子");
            return true;
        }

        var currentTurn = board.Turn;
        if (board.GenerateLegalMoves().Any()) return false;

        isGameOver = true;
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
        var result = await RunAiSearchAsync(applyBestMove: false);
        if (result == null || result.BestMove.IsNull)
        {
            ClearLatestHint();
            return new SearchResult { BestMove = Move.Null };
        }

        // StoreLatestHint 已在 RunAiSearchAsync 的 else 分支呼叫，此處無需重複呼叫
        return result;
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

            var progress = new Progress<string>(msg => ThinkingProgress?.Invoke(msg));
            var evaluations = await aiEngine.EvaluateMovesAsync(boardSnapshot, moves, SmartHintDepth, linkedCts.Token, progress);

            if (!linkedCts.Token.IsCancellationRequested)
            {
                var best = evaluations.FirstOrDefault(e => e.IsBest);
                if (best != null)
                {
                    string scoreStr    = best.Score > 0 ? $"+{best.Score}" : best.Score.ToString();
                    string bestNotation = MoveNotation.ToNotation(best.Move, board);
                    ThinkingProgress?.Invoke($"智能提示完成：最佳走法 {bestNotation} | 分數 {scoreStr} | 共 {evaluations.Count} 個走法");
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

    private static string FormatHintProgress(SearchResult result, PieceColor searchTurn, string? notation = null)
    {
        if (result.BestMove.IsNull)
        {
            return "提示：目前局面沒有可行的最佳走法";
        }

        var turnLabel = searchTurn == PieceColor.Red ? "紅方" : "黑方";
        var scoreText = FormatScore(result.Score, turnLabel);
        var moveText  = notation ?? result.BestMove.ToString();

        return $"提示完成：{moveText} | 分數: {scoreText} | 深度: {result.Depth} | 節點: {result.Nodes}";
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

    public void Dispose()
    {
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
