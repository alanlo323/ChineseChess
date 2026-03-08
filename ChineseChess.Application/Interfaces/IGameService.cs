using ChineseChess.Application.Enums;
using ChineseChess.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Interfaces;

public interface IGameService
{
    IBoard CurrentBoard { get; }
    GameMode CurrentMode { get; }
    bool IsThinking { get; }
    Move? LastMove { get; }
    bool IsSmartHintEnabled { get; set; }
    int SmartHintDepth { get; set; }
    long LastSearchNodes { get; }
    long LastSearchNps { get; }

    event Action BoardUpdated;
    event Action<string> GameMessage; // Check、Win 等訊息
    event Action<SearchResult>? HintReady; // AI 提示/分析結果
    event Action<string>? ThinkingProgress;
    event Action<IReadOnlyList<MoveEvaluation>>? SmartHintReady; // 智能提示結果

    // 控制
    Task StartGameAsync(GameMode mode);
    Task StopGameAsync();
    Task PauseThinkingAsync();
    Task ResumeThinkingAsync();
    Task ExportTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default);
    Task ImportTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default);
    Task ExportBlackTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default);
    Task ImportBlackTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default);

    // 遊戲流程
    Task HumanMoveAsync(Move move); // Player 操作
    void Undo();
    void Redo(); // 非必要（Optional，可選功能）

    // 書籤
    void AddBookmark(string name);
    void LoadBookmark(string name);
    void DeleteBookmark(string name);
    IEnumerable<string> GetBookmarks();

    // AI 難度設定
    void SetDifficulty(int depth, int timeMs, int threadCount = 0);

    // 雙AI個別設定（AiVsAi 模式）
    void SetRedAiDifficulty(int depth, int timeMs, int threadCount = 0);
    void SetBlackAiDifficulty(int depth, int timeMs, int threadCount = 0);

    // TT 共用/獨立模式（預設 false = 獨立，開局時從紅方 TT 複製一份）
    bool UseSharedTranspositionTable { get; set; }

    Task<SearchResult> GetHintAsync(); // 取得目前局面的分析結果
    Task RequestSmartHintAsync(int fromIndex, CancellationToken ct = default); // 取得指定棋子的所有走法評分

    // TT 統計
    TTStatistics GetTTStatistics();
    TTStatistics? GetBlackTTStatistics(); // 獨立TT時回傳黑方統計；共用TT時回傳 null

    // TT 合併（獨立TT模式下，將兩方 TT 雙向合併）
    Task MergeTranspositionTablesAsync(CancellationToken ct = default);
}
