using ChineseChess.Application.Enums;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Validation;
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

/// <summary>
/// 核心遊戲服務介面：走棋、事件通知、AI 控制、提示。
/// ChessBoardViewModel 等僅需棋盤互動的消費者應依賴此介面。
/// </summary>
public interface ICoreGameService
{
    IBoard CurrentBoard { get; }
    GameMode CurrentMode { get; }
    bool IsThinking { get; }
    Move? LastMove { get; }
    bool IsSmartHintEnabled { get; set; }
    int SmartHintDepth { get; set; }
    bool IsMultiPvHintEnabled { get; set; }
    int MultiPvCount { get; set; }
    long LastSearchNodes { get; }
    long LastSearchNps { get; }

    /// <summary>提示搜尋是否正在進行中。</summary>
    bool IsHintSearching { get; }

    /// <summary>提和流程是否已完成（接受或拒絕）。</summary>
    bool IsDrawOfferProcessed { get; }

    event Action BoardUpdated;
    event Action<string> GameMessage; // Check、Win 等訊息
    event Action<SearchResult>? HintReady; // AI 提示/分析結果（最終結果）
    event Action<SearchResult>? HintUpdated; // 提示搜尋進行中的即時最佳著法更新
    event Action<string>? ThinkingProgress;
    event Action<IReadOnlyList<MoveEvaluation>>? SmartHintReady; // 智能提示結果
    event Action<IReadOnlyList<MoveEvaluation>>? MultiPvHintReady; // MultiPV 提示結果
    /// <summary>AI 每步棋走完後觸發（applyBestMove=true 路徑），供局面分析使用。</summary>
    event Action<MoveCompletedEventArgs>? MoveCompleted;

    /// <summary>AI 主動提和時觸發，傳入提和資訊供玩家決策。</summary>
    event Action<DrawOfferResult>? DrawOffered;

    /// <summary>提和流程結束時觸發（接受或拒絕）。</summary>
    event Action<DrawOfferResult>? DrawOfferResolved;

    // 擺棋模式
    bool IsInSetupMode { get; }
    event Action? SetupModeChanged;
    Task EnterSetupModeAsync();
    void SetupPlacePiece(int index, Piece piece);
    void SetupRemovePiece(int index);
    void SetupClearBoard();
    void SetupResetBoard();
    void SetupSetTurn(PieceColor color);
    Task<BoardValidationResult> ConfirmSetupAsync(GameMode mode);

    // 控制
    Task StartGameAsync(GameMode mode);
    Task StopGameAsync();
    Task PauseThinkingAsync();
    Task ResumeThinkingAsync();

    // 遊戲流程
    Task HumanMoveAsync(Move move); // Player 操作
    void Undo();
    void Redo();

    // 提和流程
    /// <summary>玩家主動向 AI 提和。僅在 PlayerVsAi 模式且未結束時有效。</summary>
    Task RequestDrawAsync();

    /// <summary>回應 AI 的提和請求。</summary>
    /// <param name="accept">true = 接受和棋；false = 拒絕。</param>
    void RespondToDrawOffer(bool accept);

    // AI 難度設定
    void SetDifficulty(int depth, int timeMs, int threadCount = 0);
    void SetRedAiDifficulty(int depth, int timeMs, int threadCount = 0);
    void SetBlackAiDifficulty(int depth, int timeMs, int threadCount = 0);

    PieceColor PlayerColor { get; set; }
    bool IsTimedModeEnabled { get; set; }
    int TimedModeMinutesPerPlayer { get; set; }
    IGameClock? Clock { get; }

    Task<SearchResult> GetHintAsync();
    Task<string> ExplainLatestHintAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task RequestSmartHintAsync(int fromIndex, CancellationToken ct = default);
}

/// <summary>
/// 重播服務介面：擴充核心服務，加入棋局歷史與重播導航。
/// </summary>
public interface IReplayGameService : ICoreGameService
{
    IReadOnlyList<MoveHistoryEntry> MoveHistory { get; }
    string InitialFen { get; }
    ReplayState ReplayState { get; }
    int ReplayCurrentStep { get; }
    event Action? MoveHistoryChanged;
    event Action? ReplayStateChanged;

    Task EnterReplayModeAsync();
    Task NavigateToAsync(int step);
    Task StepForwardAsync();
    Task StepBackAsync();
    Task GoToStartAsync();
    Task GoToEndAsync();
    Task ContinueFromCurrentPositionAsync(GameMode mode);
    Task LoadGameRecordAsync(GameRecord record);
    GameRecord ExportGameRecord(string redPlayer = "玩家", string blackPlayer = "AI");
}

/// <summary>
/// 全功能遊戲服務介面：向後相容，維持 50+ 成員的完整功能。
/// ControlPanelViewModel 等需要完整功能的消費者依賴此介面。
/// </summary>
public interface IGameService : IReplayGameService
{
    // 書籤
    void AddBookmark(string name);
    void LoadBookmark(string name);
    void DeleteBookmark(string name);
    IEnumerable<string> GetBookmarks();

    // 提和設定
    void SetDrawOfferSettings(DrawOfferSettings settings);

    // TT shared/independent mode (default true = shared).
    bool UseSharedTranspositionTable { get; set; }
    bool CopyRedTtToBlackAtStart { get; set; }

    Task ExportTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default);
    Task ImportTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default);
    Task ExportBlackTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default);
    Task ImportBlackTranspositionTableAsync(string filePath, bool asJson, CancellationToken ct = default);

    // TT 統計
    TTStatistics GetTTStatistics();
    TTStatistics? GetBlackTTStatistics();
    Task MergeTranspositionTablesAsync(CancellationToken ct = default);

    // 開局庫狀態
    bool IsOpeningBookLoaded { get; }
    int OpeningBookEntryCount { get; }

    // TT 節點探索
    IEnumerable<TTEntry> EnumerateTTEntries();
    TTTreeNode? ExploreTTTree(int maxDepth = 6);

    // 殘局庫 TT 同步
    /// <summary>
    /// 將殘局庫中所有必勝/必負局面寫入 AI 引擎的 TT，
    /// 使搜尋時可直接命中殘局庫步法。
    /// 需要 <paramref name="tablebaseService"/> 已生成（非匯入）的殘局庫。
    /// </summary>
    void SyncTablebaseToTranspositionTable(ITablebaseService tablebaseService);
}
