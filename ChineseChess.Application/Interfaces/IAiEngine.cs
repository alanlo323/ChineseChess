using ChineseChess.Domain.Entities;
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
}

public interface IAiEngine
{
    Task<SearchResult> SearchAsync(IBoard board, SearchSettings settings, CancellationToken ct = default, IProgress<SearchProgress>? progress = null);
}
