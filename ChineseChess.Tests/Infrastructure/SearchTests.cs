using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

public class SearchTests
{
    [Fact]
    public async Task Search_ShouldReturnResult()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 1, TimeLimitMs = 1000 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.BestMove.IsNull);
        Assert.True(result.Nodes >= 0);
    }
}
