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
    Move? LastMove { get; }
    
    event Action BoardUpdated;
    event Action<string> GameMessage; // Check、Win 等訊息
    event Action<SearchResult>? HintReady; // AI 提示/分析結果
    event Action<string>? ThinkingProgress;

    // 控制
    Task StartGameAsync(GameMode mode);
    Task StopGameAsync();
    Task PauseThinkingAsync();
    Task ResumeThinkingAsync();
    
    // 遊戲流程
    Task HumanMoveAsync(Move move); // Player 操作
    void Undo();
    void Redo(); // 非必要（Optional，可選功能）
    
    // 書籤
    void AddBookmark(string name);
    void LoadBookmark(string name);
    void DeleteBookmark(string name);
    IEnumerable<string> GetBookmarks();
    
    // AI
    void SetDifficulty(int depth, int timeMs, int threadCount = 0);
    Task<SearchResult> GetHintAsync(); // 取得目前局面的分析結果
}
