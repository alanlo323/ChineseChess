using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// Multi-PV（多主要變例）搜尋測試。
/// 驗證 IAiEngine.SearchMultiPvAsync 的正確性：
///   - 回傳指定數量的 PV
///   - 結果按分數由高到低排序
///   - 不包含重複著法
///   - 每個 PV 包含有效著法與 PvLine
/// </summary>
public class MultiPvTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── 回傳數量 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchMultiPv_RequestThreePvs_ReturnsThreeResults()
    {
        var engine = new SearchEngine();
        var board = new Board(InitialFen);
        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 10000 };

        var results = await engine.SearchMultiPvAsync(board, settings, pvCount: 3, ct: CancellationToken.None);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task SearchMultiPv_RequestOnePv_ReturnsOneResult()
    {
        var engine = new SearchEngine();
        var board = new Board(InitialFen);
        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 10000 };

        var results = await engine.SearchMultiPvAsync(board, settings, pvCount: 1, ct: CancellationToken.None);

        Assert.Single(results);
    }

    [Fact]
    public async Task SearchMultiPv_RequestMoreThanAvailableMoves_ReturnAllMoves()
    {
        // 殘局局面：合法著法很少
        // 紅將在 (9,4)，只剩 2-3 個合法著法
        var engine = new SearchEngine();
        var board = new Board("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 5000 };
        var legalCount = board.GenerateLegalMoves().Count();

        var results = await engine.SearchMultiPvAsync(board, settings, pvCount: 100, ct: CancellationToken.None);

        // 不超過合法著法數
        Assert.True(results.Count <= legalCount,
            $"結果數 ({results.Count}) 應 <= 合法著法數 ({legalCount})");
        Assert.True(results.Count > 0, "至少要有一個結果");
    }

    // ─── 排序 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchMultiPv_Results_AreSortedByScoreDescending()
    {
        var engine = new SearchEngine();
        var board = new Board(InitialFen);
        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 10000 };

        var results = await engine.SearchMultiPvAsync(board, settings, pvCount: 5, ct: CancellationToken.None);

        for (int i = 0; i < results.Count - 1; i++)
        {
            Assert.True(results[i].Score >= results[i + 1].Score,
                $"結果 [{i}].Score ({results[i].Score}) 應 >= [{i + 1}].Score ({results[i + 1].Score})");
        }
    }

    [Fact]
    public async Task SearchMultiPv_FirstResult_IsBestFlagTrue()
    {
        var engine = new SearchEngine();
        var board = new Board(InitialFen);
        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 10000 };

        var results = await engine.SearchMultiPvAsync(board, settings, pvCount: 3, ct: CancellationToken.None);

        Assert.True(results[0].IsBest, "第一個結果的 IsBest 應為 true");
        for (int i = 1; i < results.Count; i++)
        {
            Assert.False(results[i].IsBest, $"結果 [{i}] 的 IsBest 應為 false");
        }
    }

    // ─── 著法唯一性 ───────────────────────────────────────────────────────

    [Fact]
    public async Task SearchMultiPv_Results_HaveNoDuplicateMoves()
    {
        var engine = new SearchEngine();
        var board = new Board(InitialFen);
        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 10000 };

        var results = await engine.SearchMultiPvAsync(board, settings, pvCount: 5, ct: CancellationToken.None);

        var moves = results.Select(r => r.Move).ToList();
        var distinctMoves = moves.Distinct().ToList();

        Assert.Equal(moves.Count, distinctMoves.Count);
    }

    // ─── PvLine ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchMultiPv_AllResults_HaveNonEmptyPvLine()
    {
        var engine = new SearchEngine();
        var board = new Board(InitialFen);
        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 10000 };

        var results = await engine.SearchMultiPvAsync(board, settings, pvCount: 3, ct: CancellationToken.None);

        foreach (var result in results)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.PvLine),
                $"PvLine 不應為空（著法 {result.Move}）");
        }
    }

    // ─── 有效著法 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchMultiPv_AllMoves_AreLegalMoves()
    {
        var engine = new SearchEngine();
        var board = new Board(InitialFen);
        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 10000 };

        var results = await engine.SearchMultiPvAsync(board, settings, pvCount: 5, ct: CancellationToken.None);

        var legalMoves = board.GenerateLegalMoves().ToHashSet();

        foreach (var result in results)
        {
            Assert.True(legalMoves.Contains(result.Move),
                $"著法 {result.Move} 應為合法著法");
        }
    }

    // ─── 殘局驗證 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchMultiPv_EndgamePosition_ReturnsValidResults()
    {
        // 簡單殘局：紅有車，黑只有將
        var engine = new SearchEngine();
        var board = new Board("4k4/9/9/9/9/9/9/4R4/9/4K4 w - - 0 1");
        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 5000 };

        var results = await engine.SearchMultiPvAsync(board, settings, pvCount: 3, ct: CancellationToken.None);

        Assert.True(results.Count >= 1, "殘局應至少有一個 PV");
        Assert.False(results[0].Move.IsNull, "最佳著法不應為 Null");
    }
}
