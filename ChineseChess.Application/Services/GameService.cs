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

    public GameService(IAiEngine aiEngine)
    {
        _aiEngine = aiEngine;
        _bookmarkManager = new BookmarkManager();
        _board = new Board(); // Start position
    }

    public async Task StartGameAsync(GameMode mode)
    {
        _currentMode = mode;
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
                await TriggerAiMove();
            }
        }
    }

    public Task StopGameAsync()
    {
        _aiCts?.Cancel();
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
            await TriggerAiMove();
        }
    }

    private async Task TriggerAiMove()
    {
        if (_isThinking) return;
        _isThinking = true;
        NotifyUpdate(); // Update UI to show thinking status

        _aiCts = new CancellationTokenSource();
        
        try
        {
            // Delay slightly to let UI update
            await Task.Delay(100);

            var result = await _aiEngine.SearchAsync(_board.Clone(), _aiSettings, _aiCts.Token);
            
            if (result.BestMove.IsNull)
            {
                GameMessage?.Invoke("AI Resigns (No moves)");
            }
            else
            {
                _board.MakeMove(result.BestMove);
                NotifyUpdate();
                GameMessage?.Invoke($"AI played {result.BestMove} (Score: {result.Score})");
                
                if (!CheckGameOver() && _currentMode == GameMode.AiVsAi)
                {
                    // Continue loop
                    _ = TriggerAiMove(); // Fire and forget to avoid stack overflow recursion in Task
                }
            }
        }
        catch (OperationCanceledException)
        {
            GameMessage?.Invoke("AI Search Canceled");
        }
        finally
        {
            _isThinking = false;
            NotifyUpdate();
        }
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

    public void SetDifficulty(int depth, int timeMs)
    {
        _aiSettings.Depth = depth;
        _aiSettings.TimeLimitMs = timeMs;
    }

    public Task<SearchResult> GetHintAsync()
    {
        if (_isThinking)
        {
            GameMessage?.Invoke("AI is thinking, please wait for the move to finish.");
            return Task.FromResult(new SearchResult { BestMove = Move.Null });
        }

        return GetHintInternalAsync();
    }

    private async Task<SearchResult> GetHintInternalAsync()
    {
        var result = await _aiEngine.SearchAsync(_board.Clone(), _aiSettings, CancellationToken.None);
        HintReady?.Invoke(result);
        return result;
    }

    private void NotifyUpdate() => BoardUpdated?.Invoke();
}
