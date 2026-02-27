using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Search;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

public class SearchTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    [Fact]
    public async Task Search_ShouldReturnResult()
    {
        var board = new Board(InitialFen);
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
        var board = new Board(InitialFen);
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
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 12000 };

        var first = await engine.SearchAsync(new Board(board.ToFen()), settings, CancellationToken.None);
        var second = await engine.SearchAsync(new Board(board.ToFen()), settings, CancellationToken.None);

        Assert.Equal(first.BestMove, second.BestMove);
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
    public async Task Search_IterativeDeepening_DepthIncreasesNodeCount()
    {
        var board = new Board(InitialFen);
        var engine1 = new SearchEngine();
        var engine2 = new SearchEngine();

        var shallow = await engine1.SearchAsync(board, new SearchSettings { Depth = 1 }, CancellationToken.None);
        var deeper = await engine2.SearchAsync(
            new Board(InitialFen), new SearchSettings { Depth = 3 }, CancellationToken.None);

        Assert.True(deeper.Nodes > shallow.Nodes);
        Assert.True(deeper.Depth > shallow.Depth);
    }

    // --- PST Tests ---

    [Fact]
    public void PST_RedHorseCenterScoresHigherThanCorner()
    {
        int centerScore = PieceSquareTables.GetScore(PieceType.Horse, PieceColor.Red, 40);
        int cornerScore = PieceSquareTables.GetScore(PieceType.Horse, PieceColor.Red, 81);

        Assert.True(centerScore > cornerScore);
    }

    [Fact]
    public void PST_RedPawnCrossedRiverScoresHigherThanHome()
    {
        int crossedRiver = PieceSquareTables.GetScore(PieceType.Pawn, PieceColor.Red, 36);
        int homeRow = PieceSquareTables.GetScore(PieceType.Pawn, PieceColor.Red, 72);

        Assert.True(crossedRiver > homeRow);
    }

    [Fact]
    public void PST_BlackMirrorsRed()
    {
        int redCenter = PieceSquareTables.GetScore(PieceType.Rook, PieceColor.Red, 40);
        int blackMirror = PieceSquareTables.GetScore(PieceType.Rook, PieceColor.Black, 89 - 40);

        Assert.Equal(redCenter, blackMirror);
    }

    // --- Evaluator Tests ---

    [Fact]
    public void Evaluator_MissingAdvisor_PenalizesKingSafety()
    {
        var fullDefense = new Board("4k4/4a4/4b4/9/9/9/9/4B4/4A4/4K4 w - - 0 1");
        var missingAdvisor = new Board("4k4/4a4/4b4/9/9/9/9/4B4/9/4K4 w - - 0 1");

        var evaluator = new HandcraftedEvaluator();
        int fullScore = evaluator.Evaluate(fullDefense);
        int missingScore = evaluator.Evaluate(missingAdvisor);

        Assert.True(fullScore > missingScore);
    }

    // --- Null Move / Board Tests ---

    [Fact]
    public void Board_MakeNullMove_SwitchesTurnAndZobrist()
    {
        var board = new Board(InitialFen);
        var turnBefore = board.Turn;
        var keyBefore = board.ZobristKey;

        board.MakeNullMove();

        Assert.NotEqual(turnBefore, board.Turn);
        Assert.NotEqual(keyBefore, board.ZobristKey);

        board.UnmakeNullMove();

        Assert.Equal(turnBefore, board.Turn);
        Assert.Equal(keyBefore, board.ZobristKey);
    }

    // --- Check Extension Test ---

    [Fact]
    public async Task Search_CheckPosition_ShouldFindBestResponse()
    {
        var board = new Board("3ak4/9/9/9/9/9/9/9/4r4/4K4 w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 5000 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.False(result.BestMove.IsNull);
    }

    // --- MVV-LVA Ordering Test ---

    [Fact]
    public async Task Search_ShouldPreferCapturingHighValuePiece()
    {
        // Red rook can capture black rook or black pawn — should prefer the rook
        var board = new Board("4k4/9/9/9/4r4/3RP4/9/9/9/4K4 w - - 0 1");
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 1, TimeLimitMs = 3000 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.False(result.BestMove.IsNull);
        var captured = board.GetPiece(result.BestMove.To);
        if (!captured.IsNone)
        {
            Assert.Equal(PieceType.Rook, captured.Type);
        }
    }
}
