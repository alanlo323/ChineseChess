using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Services;

public class GameService : IGameService
{
    private readonly IAiEngine _aiEngine;
    private readonly BookmarkManager _bookmarkManager;
    private Board _board;
    private GameMode _currentMode;
    private bool _isThinking;
    private SearchSettings _aiSettings = new SearchSettings();
    private CancellationTokenSource? _aiCts;
    private CancellationTokenSource? _smartHintCts;
    private readonly ManualResetEventSlim _aiPauseSignal = new ManualResetEventSlim(true);

    public IBoard CurrentBoard => _board;
    public GameMode CurrentMode => _currentMode;
    public bool IsThinking => _isThinking;
    public Move? LastMove => _board.TryGetLastMove(out var lastMove) ? lastMove : null;
    public bool IsSmartHintEnabled { get; set; } = true;
    public int SmartHintDepth { get; set; } = 2;
    public long LastSearchNodes => Interlocked.Read(ref _lastSearchNodes);
    public long LastSearchNps => Interlocked.Read(ref _lastSearchNps);
    private long _lastSearchNodes;
    private long _lastSearchNps;
    private long _completedGameNodes; // 歷史已完成搜尋的累計節點數（本局）

    public event Action? BoardUpdated;
    public event Action<string>? GameMessage;
    public event Action<SearchResult>? HintReady;
    public event Action<string>? ThinkingProgress;
    public event Action<IReadOnlyList<MoveEvaluation>>? SmartHintReady;

    public GameService(IAiEngine aiEngine)
    {
        _aiEngine = aiEngine;
        _bookmarkManager = new BookmarkManager();
        _board = new Board(); // 初始局面
    }

    public async Task StartGameAsync(GameMode mode)
    {
        _currentMode = mode;
        _aiCts?.Cancel();
        _aiPauseSignal.Set();
        _board = new Board(); // 重置為標準初始局
        Interlocked.Exchange(ref _completedGameNodes, 0);
        Interlocked.Exchange(ref _lastSearchNodes, 0);
        Interlocked.Exchange(ref _lastSearchNps, 0);
        _board.ParseFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        
        NotifyUpdate();
        GameMessage?.Invoke("Game Started");

        if (_currentMode == GameMode.AiVsAi || (_currentMode == GameMode.PlayerVsAi && _board.Turn == PieceColor.Black)) 
        {
            // 若 AI 先手（例如自訂 FEN 或 AiVsAi），就觸發 AI 下棋。
            // 一般標準局面在 PvAI 下，紅方（Player）預設先行，除非有其他設定。
            if (_currentMode == GameMode.AiVsAi)
            {
                await RunAiSearchAsync(applyBestMove: true);
            }
        }
    }

    public Task StopGameAsync()
    {
        _aiPauseSignal.Set();
        _aiCts?.Cancel();
        ThinkingProgress?.Invoke("AI 思考已停止");
        return Task.CompletedTask;
    }

    public Task PauseThinkingAsync()
    {
        if (!_isThinking)
        {
            ThinkingProgress?.Invoke("AI 目前未在思考中");
            return Task.CompletedTask;
        }

        _aiPauseSignal.Reset();
        // ThinkingProgress?.Invoke("AI 思考已暫停");
        return Task.CompletedTask;
    }

    public Task ResumeThinkingAsync()
    {
        if (!_isThinking)
        {
            ThinkingProgress?.Invoke("AI 目前未在思考中");
            return Task.CompletedTask;
        }

        _aiPauseSignal.Set();
        // ThinkingProgress?.Invoke("AI 思考已繼續");
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
            await _aiEngine.ExportTranspositionTableAsync(file, asJson, ct);
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
            await _aiEngine.ImportTranspositionTableAsync(file, asJson, ct);
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

    public async Task HumanMoveAsync(Move move)
    {
        if (_isThinking) return;
        if (_currentMode == GameMode.AiVsAi) return;
        if (move.From >= Board.BoardSize || move.To >= Board.BoardSize || move.From == move.To)
        {
            GameMessage?.Invoke("非法落子：走法資料不完整");
            return;
        }

        var legalMoves = _board.GenerateLegalMoves();
        if (!legalMoves.Contains(move))
        {
            GameMessage?.Invoke("非法落子：這不是合法著法");
            return;
        }

        _board.MakeMove(move);
        NotifyUpdate();

        if (CheckGameOver()) return;

        if (_currentMode == GameMode.PlayerVsAi)
        {
            await RunAiSearchAsync(applyBestMove: true);
        }
    }

    private async Task<SearchResult?> RunAiSearchAsync(bool applyBestMove)
    {
        if (_isThinking) return null;
        _isThinking = true;
        ThinkingProgress?.Invoke("AI 思考中...");

        _aiCts = new CancellationTokenSource();
        var cts = _aiCts;
        var boardSnapshot = _board.Clone();
        SearchResult? result = null;
        var continueAiLoop = false;
        var settings = new SearchSettings
        {
            Depth = _aiSettings.Depth,
            TimeLimitMs = _aiSettings.TimeLimitMs,
            ThreadCount = _aiSettings.ThreadCount,
            PauseSignal = _aiPauseSignal
        };
        long baseNodes = Interlocked.Read(ref _completedGameNodes);
        var progress = new Progress<SearchProgress>(p =>
        {
            Interlocked.Exchange(ref _lastSearchNodes, baseNodes + p.Nodes);
            Interlocked.Exchange(ref _lastSearchNps, p.NodesPerSecond);
            ThinkingProgress?.Invoke(FormatThinkingProgress(p));
        });
        
        try
        {
            result = await _aiEngine.SearchAsync(
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
                    var searchTurn = _board.Turn;
                    _board.MakeMove(result.BestMove);
                    NotifyUpdate();
                    GameMessage?.Invoke($"AI played {result.BestMove} (Score: {FormatScore(result.Score, searchTurn == PieceColor.Red ? "紅方" : "黑方")})");
                    ThinkingProgress?.Invoke(FormatHintProgress(result, searchTurn));
                }
                else
                {
                    HintReady?.Invoke(result);
                    ThinkingProgress?.Invoke(FormatHintProgress(result, _board.Turn));
                }
                
                if (applyBestMove && !CheckGameOver() && _currentMode == GameMode.AiVsAi)
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
            _isThinking = false;
            if (result != null)
            {
                Interlocked.Add(ref _completedGameNodes, result.Nodes);
                Interlocked.Exchange(ref _lastSearchNodes, Interlocked.Read(ref _completedGameNodes));
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

    private bool CheckGameOver()
    {
        var currentTurn = _board.Turn;
        if (_board.GenerateLegalMoves().Any()) return false;

        var winner = currentTurn == PieceColor.Red ? "黑方" : "紅方";
        if (_board.IsCheck(currentTurn))
            GameMessage?.Invoke($"將死！{winner}獲勝！");
        else
            GameMessage?.Invoke($"困斃！{winner}獲勝！"); // 中國象棋中困斃也算輸

        return true;
    }

    public void Undo()
    {
        if (_isThinking) return;

        bool didUndo = false;

        void TryUndo()
        {
            if (!_board.TryGetLastMove(out _))
            {
                GameMessage?.Invoke("無可悔棋步數");
                return;
            }

            try
            {
                _board.UndoMove();
                didUndo = true;
            }
            catch (InvalidOperationException)
            {
                GameMessage?.Invoke("悔棋失敗");
            }
        }

        TryUndo();
        if (_currentMode == GameMode.PlayerVsAi)
        {
            if (_board.TryGetLastMove(out _))
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

    public void AddBookmark(string name) => _bookmarkManager.AddBookmark(name, _board.ToFen());
    public void LoadBookmark(string name)
    {
        var fen = _bookmarkManager.GetBookmark(name);
        if (fen != null)
        {
            _board.ParseFen(fen);
            NotifyUpdate();
        }
    }
    public void DeleteBookmark(string name) => _bookmarkManager.DeleteBookmark(name);
    public IEnumerable<string> GetBookmarks() => _bookmarkManager.GetBookmarkNames();

    public void SetDifficulty(int depth, int timeMs, int threadCount = 0)
    {
        _aiSettings.Depth = depth;
        _aiSettings.TimeLimitMs = timeMs;
        if (threadCount > 0) _aiSettings.ThreadCount = threadCount;
    }

    public async Task<SearchResult> GetHintAsync()
    {
        var result = await RunAiSearchAsync(applyBestMove: false);
        return result ?? new SearchResult { BestMove = Move.Null };
    }

    public TTStatistics GetTTStatistics() => _aiEngine.GetTTStatistics();

    public async Task RequestSmartHintAsync(int fromIndex, CancellationToken ct = default)
    {
        if (!IsSmartHintEnabled) return;

        // 取消上一次尚未完成的智能提示搜尋
        _smartHintCts?.Cancel();
        _smartHintCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _smartHintCts.Token);

        try
        {
            var board = _board.Clone();
            var moves = board.GenerateLegalMoves()
                .Where(m => m.From == fromIndex)
                .ToList();

            if (moves.Count == 0) return;

            ThinkingProgress?.Invoke($"智能提示：開始分析 {moves.Count} 個走法（深度 {SmartHintDepth}）...");

            var progress = new Progress<string>(msg => ThinkingProgress?.Invoke(msg));
            var evaluations = await _aiEngine.EvaluateMovesAsync(board, moves, SmartHintDepth, linkedCts.Token, progress);

            if (!linkedCts.Token.IsCancellationRequested)
            {
                var best = evaluations.FirstOrDefault(e => e.IsBest);
                if (best != null)
                {
                    string scoreStr = best.Score > 0 ? $"+{best.Score}" : best.Score.ToString();
                    ThinkingProgress?.Invoke($"智能提示完成：最佳走法 {best.Move} | 分數 {scoreStr} | 共 {evaluations.Count} 個走法");
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
        var turnLabel = _board.Turn == PieceColor.Red ? "紅方" : "黑方";
        var scoreText = FormatScore(progress.Score, turnLabel);
        var bestMove = string.IsNullOrWhiteSpace(progress.BestMove) ? "待更新" : progress.BestMove;
        var mode = progress.IsHeartbeat ? "（即時）" : "（階段）";

        return $"AI 思考中{mode}：深度 {progress.CurrentDepth}/{progress.MaxDepth}，耗時 {elapsedSeconds}，節點 {progress.Nodes}（{speed}），分數 {scoreText}，建議 {bestMove}";
    }

    private static string FormatHintProgress(SearchResult result, PieceColor searchTurn)
    {
        if (result.BestMove.IsNull)
        {
            return "提示：目前局面沒有可行的最佳走法";
        }

        var turnLabel = searchTurn == PieceColor.Red ? "紅方" : "黑方";
        var scoreText = FormatScore(result.Score, turnLabel);

        return $"提示完成：{result.BestMove} | 分數: {scoreText} | 深度: {result.Depth} | 節點: {result.Nodes}";
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
        if (!_isThinking)
        {
            return;
        }

        _aiPauseSignal.Set();
        _aiCts?.Cancel();
        while (_isThinking)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }
}
