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
}

public class SearchResult
{
    public Move BestMove { get; set; }
    public int Score { get; set; }
    public int Depth { get; set; }
    public long Nodes { get; set; }
    public string PvLine { get; set; } = string.Empty;
}

public class SearchSettings
{
    public int Depth { get; set; } = 5;
    public int TimeLimitMs { get; set; } = 3000;
    public int ThreadCount { get; set; } = Environment.ProcessorCount;
    public ManualResetEventSlim PauseSignal { get; set; } = new ManualResetEventSlim(true);
}

public class MoveEvaluation
{
    public Move Move { get; set; }
    public int Score { get; set; }  // 從「做出此走法的玩家」視角，正分 = 對自己有利
    public bool IsBest { get; set; }
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
    Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default);
    Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default);
    TTStatistics GetTTStatistics();

    /// <summary>建立一個新引擎，其 TT 為本引擎 TT 的深度複製（獨立 TT 模式用）。</summary>
    IAiEngine CloneWithCopiedTT();

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
