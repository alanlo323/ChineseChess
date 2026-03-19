using ChineseChess.Application.Enums;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;

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
    /// <summary>AI 每步棋走完後觸發（applyBestMove=true 路徑），供局面分析使用。</summary>
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

    // ─── PlayerVsAi 玩家顏色 ──────────────────────────────────────────────
    /// <summary>PlayerVsAi 模式下玩家扮演的顏色。</summary>
    PieceColor PlayerColor { get; set; }

    // ─── 限時模式 ─────────────────────────────────────────────────────────
    bool IsTimedModeEnabled { get; set; }
    int TimedModeMinutesPerPlayer { get; set; }
    /// <summary>棋鐘實例。僅限時模式開局後有值，否則為 null。</summary>
    IGameClock? Clock { get; }

    // ─── 棋局歷史與重播 ──────────────────────────────────────────────────────

    /// <summary>本局完整走法歷史（Live 模式下為最新局面；重播中為重播前的完整歷史）。</summary>
    IReadOnlyList<MoveHistoryEntry> MoveHistory { get; }

    /// <summary>初始局面 FEN（StartGameAsync 時記錄）。</summary>
    string InitialFen { get; }

    /// <summary>目前重播狀態。</summary>
    ReplayState ReplayState { get; }

    /// <summary>重播模式下目前定格的步號（0 = 初始局面，= MoveHistory.Count 時等同最新）。</summary>
    int ReplayCurrentStep { get; }

    /// <summary>走法歷史變更時觸發（追加、移除、載入棋局）。</summary>
    event Action? MoveHistoryChanged;

    /// <summary>重播狀態切換時觸發（Live ↔ Replaying ↔ Branching）。</summary>
    event Action? ReplayStateChanged;

    /// <summary>進入重播模式（等待 AI 完全停止後再切換）。</summary>
    Task EnterReplayModeAsync();

    /// <summary>跳躍至第 step 步後狀態（0 = 初始局面，重建棋盤與 wxfHistory）。</summary>
    Task NavigateToAsync(int step);

    /// <summary>前進一步（重播模式中）。</summary>
    Task StepForwardAsync();

    /// <summary>後退一步（重播模式中）。</summary>
    Task StepBackAsync();

    /// <summary>跳至初始局面（重播模式中）。</summary>
    Task GoToStartAsync();

    /// <summary>跳至最新局面並恢復 Live 模式。</summary>
    Task GoToEndAsync();

    /// <summary>
    /// 從目前重播局面繼續對弈（中途換手）。
    /// 截斷 MoveHistory 至目前步，切換對局模式為 mode。
    /// </summary>
    Task ContinueFromCurrentPositionAsync(GameMode mode);

    /// <summary>載入外部 GameRecord 進入重播模式。</summary>
    Task LoadGameRecordAsync(GameRecord record);

    /// <summary>將目前棋局匯出為 GameRecord。</summary>
    GameRecord ExportGameRecord(string redPlayer = "玩家", string blackPlayer = "AI");

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
