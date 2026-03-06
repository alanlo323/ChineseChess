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
}
