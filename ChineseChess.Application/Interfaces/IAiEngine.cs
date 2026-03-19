using ChineseChess.Domain.Entities;
using System.Collections.Generic;
using System.IO;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Interfaces;

public class SearchProgress
{
    public int CurrentDepth { get; set; }
    public int MaxDepth { get; set; }
    public long Nodes { get; set; }
    public int Score { get; set; }
    public string? BestMove { get; set; }
    public long ElapsedMs { get; set; }
    public long NodesPerSecond { get; set; }
    public bool IsHeartbeat { get; set; }
    public string? Message { get; set; }
    public int ThreadCount { get; set; }
    public double TtHitRate { get; set; }
    /// <summary>目前最佳著法的起始格（0–89），無最佳著法時為 -1。</summary>
    public int BestMoveFrom { get; set; } = -1;
    /// <summary>目前最佳著法的目標格（0–89），無最佳著法時為 -1。</summary>
    public int BestMoveTo { get; set; } = -1;
}

public class SearchResult
{
    public Move BestMove { get; set; }
    public int Score { get; set; }
    public int Depth { get; set; }
    public long Nodes { get; set; }
    public string PvLine { get; set; } = string.Empty;
    /// <summary>此結果是否來自開局庫（非 AI 搜尋）。</summary>
    public bool IsFromOpeningBook { get; set; }
}

public class SearchSettings
{
    public int Depth { get; set; } = 5;
    /// <summary>
    /// 原有時間限制（毫秒）。向下相容：若未設定 HardTimeLimitMs，則以此值作為硬時限。
    /// </summary>
    public int TimeLimitMs { get; set; } = 3000;
    /// <summary>
    /// 軟時限（毫秒）：在完成每一整層後檢查是否超過此時限。
    /// null 表示不使用軟時限（僅靠 Depth 上限或 HardTimeLimitMs 停止）。
    /// </summary>
    public int? SoftTimeLimitMs { get; set; } = null;
    /// <summary>
    /// 硬時限（毫秒）：到達時強制中途取消搜尋。
    /// null 表示採用 TimeLimitMs（向下相容）。
    /// </summary>
    public int? HardTimeLimitMs { get; set; } = null;
    public int ThreadCount { get; set; } = Environment.ProcessorCount;
    public ManualResetEventSlim? PauseSignal { get; set; } = null;

    /// <summary>
    /// 取得實際硬時限（毫秒）：HardTimeLimitMs ?? TimeLimitMs（向下相容）。
    /// </summary>
    public int EffectiveHardLimitMs => HardTimeLimitMs ?? TimeLimitMs;

    /// <summary>
    /// 是否允許開局庫 Decorator 在此次搜尋中查詢開局庫。
    /// hint 模式（GetHintAsync）由呼叫端設為 false；正式走棋預設為 true。
    /// </summary>
    public bool AllowOpeningBook { get; set; } = true;
}

public class MoveEvaluation
{
    public Move Move { get; set; }
    public int Score { get; set; }  // 從「做出此走法的玩家」視角，正分 = 對自己有利
    public bool IsBest { get; set; }
    /// <summary>從此著法出發的主要變例序列（符號表示法，空格分隔）。</summary>
    public string PvLine { get; set; } = string.Empty;
}

public enum TTFlag : byte
{
    None = 0,
    Exact = 1,
    LowerBound = 2,
    UpperBound = 3
}

public struct TTEntry
{
    public ulong Key;
    public short Score;
    public byte Depth;
    public TTFlag Flag;
    public Move BestMove;
    public byte Generation;
}

public class TTStatistics
{
    public ulong Capacity { get; set; }          // 表格容量（條目數）
    public double MemoryMb { get; set; }         // 記憶體用量（MB）
    public byte Generation { get; set; }         // 當前世代（每局遞增）
    public long TotalProbes { get; set; }        // 累計查詢次數
    public long Hits { get; set; }               // 命中次數
    public double HitRate { get; set; }          // 命中率（0.0–1.0）
    public long OccupiedEntries { get; set; }    // 已佔用條目數（有效節點）
    public double FillRate { get; set; }         // 填滿率（0.0–1.0）
    public long CollisionCount { get; set; }     // QP 探測碰撞次數（遇到不同 key 的非空槽位）
    public double CollisionRate { get; set; }    // 碰撞率（CollisionCount / TotalProbes）
}

/// <summary>
/// TT 樹狀探索的單一節點。
/// </summary>
public class TTTreeNode
{
    public TTEntry Entry { get; init; }
    /// <summary>從父節點走到此節點所執行的走法。根節點此欄位為預設值（無意義）。</summary>
    public Move MoveToHere { get; init; }
    public List<TTTreeNode> Children { get; init; } = [];
}

public interface IAiEngine
{
    Task<SearchResult> SearchAsync(IBoard board, SearchSettings settings, CancellationToken ct = default, IProgress<SearchProgress>? progress = null);
    Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesAsync(IBoard board, IEnumerable<Move> moves, int depth, CancellationToken ct = default, IProgress<string>? progress = null);

    /// <summary>
    /// 搜尋多個最佳著法（Multi-PV 模式）。
    /// 回傳前 <paramref name="pvCount"/> 個最佳著法，每個含分數與 PV 走法序列。
    /// 結果按分數由高到低排序，不含重複著法。
    /// </summary>
    Task<IReadOnlyList<MoveEvaluation>> SearchMultiPvAsync(
        IBoard board, SearchSettings settings, int pvCount,
        CancellationToken ct = default, IProgress<SearchProgress>? progress = null);
    Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default);
    Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default);
    TTStatistics GetTTStatistics();

    /// <summary>建立一個新引擎，其 TT 為本引擎 TT 的深度複製（獨立 TT 模式用）。</summary>
    IAiEngine CloneWithCopiedTT();

    /// <summary>建立一個新引擎，其 TT 與本引擎同大小但為空白（獨立 TT 模式可選）。</summary>
    IAiEngine CloneWithEmptyTT();

    /// <summary>將 <paramref name="other"/> 引擎的 TT 以深度優先策略合併進本引擎的 TT。</summary>
    void MergeTranspositionTableFrom(IAiEngine other);

    /// <summary>
    /// 枚舉 TT 中所有有效條目（惰性求值）。
    /// 適合統計分析，不保證執行緒安全（讀取時 TT 可能仍在更新）。
    /// </summary>
    IEnumerable<TTEntry> EnumerateTTEntries();

    /// <summary>
    /// 從 <paramref name="board"/> 當前局面出發，沿 TT 中 BestMove 連結遞迴追蹤，
    /// 建立搜尋樹節點結構。若當前局面不在 TT 中，回傳 <c>null</c>。
    /// </summary>
    TTTreeNode? ExploreTTTree(IBoard board, int maxDepth = 6);
}
