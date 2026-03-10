using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Services;

public class GameService : IGameService
{
    private readonly IAiEngine aiEngine;          // 紅方（或共用）引擎
    private IAiEngine? aiEngineBlack;             // 黑方引擎（獨立TT模式才有值）
    private readonly BookmarkManager bookmarkManager;
    private Board board;
    private GameMode currentMode;
    private bool isThinking;
    private SearchSettings redAiSettings  = new SearchSettings();
    private SearchSettings blackAiSettings = new SearchSettings();
    private CancellationTokenSource? aiCts;
    private CancellationTokenSource? smartHintCts;
    private readonly ManualResetEventSlim aiPauseSignal = new ManualResetEventSlim(true);

    public IBoard CurrentBoard => board;
    public GameMode CurrentMode => currentMode;
    public bool IsThinking => isThinking;
    public Move? LastMove => board.TryGetLastMove(out var lastMove) ? lastMove : null;
    public bool IsSmartHintEnabled { get; set; } = true;
    public int SmartHintDepth { get; set; } = 2;
    public long LastSearchNodes => Interlocked.Read(ref lastSearchNodes);
    public long LastSearchNps => Interlocked.Read(ref lastSearchNps);
    public bool UseSharedTranspositionTable { get; set; } = false;
    private long lastSearchNodes;
    private long lastSearchNps;
    private long completedGameNodes; // 歷史已完成搜尋的累計節點數（本局）

    public event Action? BoardUpdated;
    public event Action<string>? GameMessage;
    public event Action<SearchResult>? HintReady;
    public event Action<string>? ThinkingProgress;
    public event Action<IReadOnlyList<MoveEvaluation>>? SmartHintReady;

    public GameService(IAiEngine aiEngine)
    {
        this.aiEngine = aiEngine;
        bookmarkManager = new BookmarkManager();
        board = new Board(); // 初始局面
    }

    public async Task StartGameAsync(GameMode mode)
    {
        currentMode = mode;
        aiCts?.Cancel();
        aiPauseSignal.Set();
        board = new Board(); // 重置為標準初始局
        isGameOver = false;
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
        NotifyUpdate();

        if (CheckGameOver()) return;

        if (currentMode == GameMode.PlayerVsAi)
        {
            await RunAiSearchAsync(applyBestMove: true);
        }
    }

    private async Task<SearchResult?> RunAiSearchAsync(bool applyBestMove)
    {
        if (isThinking) return null;
        isThinking = true;
        ThinkingProgress?.Invoke("AI 思考中...");

        aiCts = new CancellationTokenSource();
        var cts = aiCts;
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
                    NotifyUpdate();
                    GameMessage?.Invoke($"AI 走了 {moveNotation}（分數：{FormatScore(result.Score, searchTurn == PieceColor.Red ? "紅方" : "黑方")}）");
                    ThinkingProgress?.Invoke(FormatHintProgress(result, searchTurn, moveNotation));
                }
                else
                {
                    var moveNotation = MoveNotation.ToNotation(result.BestMove, board);
                    HintReady?.Invoke(result);
                    ThinkingProgress?.Invoke(FormatHintProgress(result, board.Turn, moveNotation));
                }

                if (applyBestMove && !CheckGameOver() && currentMode == GameMode.AiVsAi)
                {
                    // 繼續執行下一輪
                    continueAiLoop = true;
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
            isThinking = false;
            if (result != null)
            {
                Interlocked.Add(ref completedGameNodes, result.Nodes);
                Interlocked.Exchange(ref lastSearchNodes, Interlocked.Read(ref completedGameNodes));
            }
            if (applyBestMove)
            {
                NotifyUpdate();
            }
        }

        if (continueAiLoop)
        {
            _ = RunAiSearchAsync(applyBestMove: true);
        }

        return result;
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
            NotifyUpdate();
        }
    }
    public void DeleteBookmark(string name) => bookmarkManager.DeleteBookmark(name);
    public IEnumerable<string> GetBookmarks() => bookmarkManager.GetBookmarkNames();

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
        return result ?? new SearchResult { BestMove = Move.Null };
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

        // 取消上一次尚未完成的智能提示搜尋
        smartHintCts?.Cancel();
        smartHintCts = new CancellationTokenSource();
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

        return $"AI 思考中{mode}：深度 {progress.CurrentDepth}/{progress.MaxDepth}，耗時 {elapsedSeconds}，節點 {progress.Nodes}（{speed}），分數 {scoreText}，建議 {bestMove}";
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
