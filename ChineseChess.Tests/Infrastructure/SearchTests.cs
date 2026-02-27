using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Search;
using System.Collections.Generic;
using System.Linq;
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

    [Fact]
    public async Task Search_ShouldReportHeartbeatProgress()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 12000 };
        var reports = new List<SearchProgress>();

        var result = await engine.SearchAsync(
            board,
            settings,
            CancellationToken.None,
            new Progress<SearchProgress>(p => reports.Add(p)));

        Assert.NotNull(result);
        Assert.True(reports.Count > 0);
        Assert.True(reports.Exists(p => p.IsHeartbeat));
        Assert.True(reports.Exists(p => !p.IsHeartbeat));
        Assert.All(reports, p => Assert.True(p.ElapsedMs >= 0));
        Assert.All(reports, p => Assert.True(p.NodesPerSecond >= 0));
    }

    [Fact]
    public async Task Search_AsymmetricPosition_ShouldReturnNonZeroScore()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABN1 w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 12000 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.NotEqual(0, result.Score);
        Assert.False(result.BestMove.IsNull);
    }

    [Fact]
    public async Task Search_ShouldReturnSameMove_ForSamePosition()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 12000 };

        var first = await engine.SearchAsync(new Board(board.ToFen()), settings, CancellationToken.None);
        var second = await engine.SearchAsync(new Board(board.ToFen()), settings, CancellationToken.None);

        Assert.Equal(first.BestMove, second.BestMove);
    }

    [Fact]
    public async Task Search_DepthOne_ShouldPreferHigherPriorityMoveInEqualScore()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 1, TimeLimitMs = 12000 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        var legalMoves = board.GenerateLegalMoves().ToList();
        var expectedMove = legalMoves
            .OrderByDescending(m => MovePriority(board, m))
            .ThenByDescending(m => m.To)
            .First();

        Assert.Equal(expectedMove, result.BestMove);
    }

    [Fact]
    public async Task Search_WithCaptureQuiescence_ShouldReturnResult()
    {
        var board = new Board("4k4/9/9/9/4p4/4P4/9/9/9/4K4 w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 1, TimeLimitMs = 3000 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.BestMove.IsNull);
    }

    [Fact]
    public void MovePriority_ShouldApplyCaptureScoreFormulaCorrectly()
    {
        var board = new Board("k8/9/9/9/9/9/9/n2p5/R3A4 w - - 0 1");
        var rookCapture = new Move(81, 72); // Rook captures a horse
        var advisorCapture = new Move(85, 75); // Advisor captures a pawn

        int rookScore = MovePriority(board, rookCapture);
        int advisorScore = MovePriority(board, advisorCapture);
        int expectedRookScore = 900 + 10000 + (PieceValue(PieceType.Horse) - PieceValue(PieceType.Rook)) / 2;
        int expectedAdvisorScore = 800 + 10000 + (PieceValue(PieceType.Pawn) - PieceValue(PieceType.Advisor)) / 2;

        Assert.Equal(expectedRookScore, rookScore);
        Assert.Equal(expectedAdvisorScore, advisorScore);
        Assert.True(advisorScore > rookScore);
    }

    private static int MovePriority(IBoard board, Move move)
    {
        var movingPiece = board.GetPiece(move.From);
        var targetPiece = board.GetPiece(move.To);

        if (movingPiece.IsNone)
        {
            return int.MinValue;
        }

        int score = movingPiece.Type switch
        {
            PieceType.King => 9000,
            PieceType.Advisor => 800,
            PieceType.Elephant => 780,
            PieceType.Horse => 700,
            PieceType.Rook => 900,
            PieceType.Cannon => 650,
            PieceType.Pawn => 300,
            _ => 0
        };

        if (!targetPiece.IsNone)
        {
            score += 10000;
            score += (PieceValue(targetPiece.Type) - PieceValue(movingPiece.Type)) / 2;
        }

        return score;
    }

    private static int PieceValue(PieceType type)
    {
        return type switch
        {
            PieceType.King => 10000,
            PieceType.Advisor => 120,
            PieceType.Elephant => 120,
            PieceType.Horse => 270,
            PieceType.Rook => 600,
            PieceType.Cannon => 285,
            PieceType.Pawn => 30,
            _ => 0
        };
    }
}
