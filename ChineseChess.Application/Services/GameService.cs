using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System;
using System.Collections.Generic;
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

    public IBoard CurrentBoard => _board;
    public GameMode CurrentMode => _currentMode;
    public bool IsThinking => _isThinking;

    public event Action? BoardUpdated;
    public event Action<string>? GameMessage;
    public event Action<SearchResult>? HintReady;
    public event Action<string>? ThinkingProgress;

    public GameService(IAiEngine aiEngine)
    {
        _aiEngine = aiEngine;
        _bookmarkManager = new BookmarkManager();
        _board = new Board(); // Start position
    }

    public async Task StartGameAsync(GameMode mode)
    {
        _currentMode = mode;
        _aiCts?.Cancel();
        _board = new Board(); // Reset to standard start
        _board.ParseFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        
        NotifyUpdate();
        GameMessage?.Invoke("Game Started");

        if (_currentMode == GameMode.AiVsAi || (_currentMode == GameMode.PlayerVsAi && _board.Turn == PieceColor.Black)) 
        {
            // If AI starts (e.g. customized FEN or AiVsAi), trigger it.
            // For standard start, Red (Player) moves first in PvAI unless configured otherwise.
            if (_currentMode == GameMode.AiVsAi)
            {
                await RunAiSearchAsync(applyBestMove: true);
            }
        }
    }

    public Task StopGameAsync()
    {
        _aiCts?.Cancel();
        ThinkingProgress?.Invoke("AI 思考已停止");
        return Task.CompletedTask;
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
        var progress = new Progress<SearchProgress>(p =>
        {
            ThinkingProgress?.Invoke(FormatThinkingProgress(p));
        });
        
        try
        {
            result = await _aiEngine.SearchAsync(
                boardSnapshot,
                _aiSettings,
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
                    // Continue loop
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
        // Check Mate or Stalemate
        // var moves = _board.GenerateLegalMoves();
        // if (!moves.Any()) { ... Win/Draw logic ... return true; }
        return false;
    }

    public void Undo()
    {
        if (_isThinking) return;
        try
        {
            _board.UndoMove();
            // If PvAI, undo twice to get back to player turn?
            if (_currentMode == GameMode.PlayerVsAi)
            {
                _board.UndoMove();
            }
            NotifyUpdate();
        }
        catch { }
    }

    public void Redo()
    {
        // Requires separate history stack in GameService
        throw new NotImplementedException();
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
}
