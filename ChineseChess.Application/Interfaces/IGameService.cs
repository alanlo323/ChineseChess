using ChineseChess.Application.Enums;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Interfaces;

/// <summary>AI 走棋完成後觸發，傳遞局面與搜尋結果供分析使用。</summary>
public sealed record MoveCompletedEventArgs
{
    public string Fen { get; init; } = string.Empty;
    public string MoveNotation { get; init; } = string.Empty;
    public int Score { get; init; }
    public int Depth { get; init; }
    public long Nodes { get; init; }
    public string? PvLine { get; init; }
    public PieceColor MovedBy { get; init; }
    public int MoveNumber { get; init; }
}

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

    /// <summary>提和流程是否已完成（接受或拒絕）。</summary>
    bool IsDrawOfferProcessed { get; }

    event Action BoardUpdated;
    event Action<string> GameMessage; // Check、Win 等訊息
    event Action<SearchResult>? HintReady; // AI 提示/分析結果（最終結果）
    event Action<SearchResult>? HintUpdated; // 提示搜尋進行中的即時最佳著法更新
    event Action<string>? ThinkingProgress;
    event Action<IReadOnlyList<MoveEvaluation>>? SmartHintReady; // 智能提示結果
    /// <summary>AI 每步棋走完後觸發（applyBestMove=true 路徑），僅在 AiVsAi 模式下供局面分析使用。</summary>
    event Action<MoveCompletedEventArgs>? MoveCompleted;

    /// <summary>提示搜尋是否正在進行中。</summary>
    bool IsHintSearching { get; }

    /// <summary>AI 主動提和時觸發，傳入提和資訊供玩家決策。</summary>
    event Action<DrawOfferResult>? DrawOffered;

    /// <summary>提和流程結束時觸發（接受或拒絕）。</summary>
    event Action<DrawOfferResult>? DrawOfferResolved;

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

    // 提和流程
    /// <summary>玩家主動向 AI 提和。僅在 PlayerVsAi 模式且未結束時有效。</summary>
    Task RequestDrawAsync();

    /// <summary>回應 AI 的提和請求。</summary>
    /// <param name="accept">true = 接受和棋；false = 拒絕。</param>
    void RespondToDrawOffer(bool accept);

    /// <summary>（測試用）模擬 AI 已提和，設定待回應狀態。</summary>
    void SimulateAiDrawOffer();

    // 書籤
    void AddBookmark(string name);
    void LoadBookmark(string name);
    void DeleteBookmark(string name);
    IEnumerable<string> GetBookmarks();

    // 提和設定
    void SetDrawOfferSettings(DrawOfferSettings settings);

    // AI 難度設定
    void SetDifficulty(int depth, int timeMs, int threadCount = 0);

    // 雙AI個別設定（AiVsAi 模式）
    void SetRedAiDifficulty(int depth, int timeMs, int threadCount = 0);
    void SetBlackAiDifficulty(int depth, int timeMs, int threadCount = 0);

    // TT shared/independent mode (default true = shared).
    bool UseSharedTranspositionTable { get; set; }
    // Independent TT mode: whether black engine should copy red TT at game start.
    bool CopyRedTtToBlackAtStart { get; set; }

    Task<SearchResult> GetHintAsync(); // 取得目前局面的分析結果
    Task<string> ExplainLatestHintAsync(IProgress<string>? progress = null, CancellationToken ct = default); // 解釋最新提示
    Task RequestSmartHintAsync(int fromIndex, CancellationToken ct = default); // 取得指定棋子的所有走法評分

    // TT 統計
    TTStatistics GetTTStatistics();
    TTStatistics? GetBlackTTStatistics(); // 獨立TT時回傳黑方統計；共用TT時回傳 null

    // TT 合併（獨立TT模式下，將兩方 TT 雙向合併）
    Task MergeTranspositionTablesAsync(CancellationToken ct = default);

    // 開局庫狀態
    /// <summary>開局庫是否已載入（至少含一個局面）。</summary>
    bool IsOpeningBookLoaded { get; }

    /// <summary>開局庫中的局面總數。</summary>
    int OpeningBookEntryCount { get; }

    // TT 節點探索
    /// <summary>枚舉紅方（或共用）TT 中所有有效條目。</summary>
    IEnumerable<TTEntry> EnumerateTTEntries();

    /// <summary>
    /// 從當前局面出發，沿 TT BestMove 連結建立探索樹。
    /// 若當前局面不在 TT 中，回傳 <c>null</c>。
    /// </summary>
    TTTreeNode? ExploreTTTree(int maxDepth = 6);
}
