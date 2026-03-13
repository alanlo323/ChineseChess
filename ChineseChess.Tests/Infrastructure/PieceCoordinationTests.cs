using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 L2 棋子協同評估功能：
///   - 馬炮同列/同排配合加分
///   - 車炮同列沈底威脅加分
/// </summary>
public class PieceCoordinationTests
{
    // ─── 馬炮協同 ──────────────────────────────────────────────────────────

    [Fact]
    public void HorseCannon_SameRow_GetsCoordinationBonus()
    {
        // 馬和炮在同一排，應有協同加分
        // 紅馬在 (6,2)，紅炮在 (6,6)，同排
        var boardCoord = new Board("4k4/9/9/9/9/9/2N3C2/9/9/4K4 w - - 0 1");
        // 馬和炮不同排、不同列：無協同
        var boardNoCoord = new Board("4k4/9/9/9/9/9/2N6/9/3C5/4K4 w - - 0 1");

        int coordScore = PieceCoordination.Evaluate(boardCoord, PieceColor.Red);
        int noCoordScore = PieceCoordination.Evaluate(boardNoCoord, PieceColor.Red);

        Assert.True(coordScore > noCoordScore,
            $"馬炮同排協同 ({coordScore}) 應高於非協同 ({noCoordScore})");
    }

    [Fact]
    public void HorseCannon_SameColumn_GetsCoordinationBonus()
    {
        // 馬和炮在同一列，應有協同加分
        // 紅馬在 (5,4)，紅炮在 (2,4)，同列
        var boardCoord = new Board("4k4/9/4C4/9/9/4N4/9/9/9/4K4 w - - 0 1");
        var boardNoCoord = new Board("4k4/9/4C4/9/9/9/9/5N3/9/4K4 w - - 0 1");

        int coordScore = PieceCoordination.Evaluate(boardCoord, PieceColor.Red);
        int noCoordScore = PieceCoordination.Evaluate(boardNoCoord, PieceColor.Red);

        Assert.True(coordScore > noCoordScore,
            $"馬炮同列協同 ({coordScore}) 應高於非協同 ({noCoordScore})");
    }

    // ─── 車炮同列沈底威脅 ─────────────────────────────────────────────────

    [Fact]
    public void RookCannon_SameColumn_GetsCoordinationBonus()
    {
        // 車和炮在同一列，形成雙重威脅（沈底配合）
        // 紅車在 (1,4)，紅炮在 (5,4)，同列
        var boardCoord = new Board("4k4/4R4/9/9/9/4C4/9/9/9/4K4 w - - 0 1");
        var boardNoCoord = new Board("4k4/4R4/9/9/9/9/9/9/3C5/4K4 w - - 0 1");

        int coordScore = PieceCoordination.Evaluate(boardCoord, PieceColor.Red);
        int noCoordScore = PieceCoordination.Evaluate(boardNoCoord, PieceColor.Red);

        Assert.True(coordScore > noCoordScore,
            $"車炮同列協同 ({coordScore}) 應高於非協同 ({noCoordScore})");
    }

    // ─── 基本屬性 ─────────────────────────────────────────────────────────

    [Fact]
    public void PieceCoordination_NoSpecialPieces_ReturnsZero()
    {
        // 只有帥將，無特殊棋子協同
        var board = new Board("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        int score = PieceCoordination.Evaluate(board, PieceColor.Red);

        Assert.Equal(0, score);
    }

    [Fact]
    public void PieceCoordination_DoesNotMutateBoard()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var originalFen = board.ToFen();

        PieceCoordination.Evaluate(board, PieceColor.Red);
        PieceCoordination.Evaluate(board, PieceColor.Black);

        Assert.Equal(originalFen, board.ToFen());
    }

    // ─── 評估器整合 ───────────────────────────────────────────────────────

    [Fact]
    public void HandcraftedEvaluator_WithPieceCoordination_ReturnsConsistentScore()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var evaluator = new HandcraftedEvaluator();

        int first = evaluator.Evaluate(board);
        int second = evaluator.Evaluate(board);

        Assert.Equal(first, second);
    }
}
