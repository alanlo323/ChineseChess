using ChineseChess.Application.Enums;
using ChineseChess.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ChineseChess.Application.Interfaces;

public interface IGameService
{
    IBoard CurrentBoard { get; }
    GameMode CurrentMode { get; }
    bool IsThinking { get; }
    
    event Action BoardUpdated;
    event Action<string> GameMessage; // Check, Win, etc.
    event Action<SearchResult>? HintReady; // AI hint/analysis result
    event Action<string>? ThinkingProgress;

    // Control
    Task StartGameAsync(GameMode mode);
    Task StopGameAsync();
    
    // Gameplay
    Task HumanMoveAsync(Move move); // Player move
    void Undo();
    void Redo(); // Optional
    
    // Bookmarks
    void AddBookmark(string name);
    void LoadBookmark(string name);
    void DeleteBookmark(string name);
    IEnumerable<string> GetBookmarks();
    
    // AI
    void SetDifficulty(int depth, int timeMs);
    Task<SearchResult> GetHintAsync(); // Get analysis for current position
}
