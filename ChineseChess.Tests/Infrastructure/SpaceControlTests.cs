using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 L3 空間控制評估功能：
///   - 統計己方棋子能攻擊的敵方半場格數
///   - 給予微幅加成
///   - 不修改棋盤狀態
/// </summary>
public class SpaceControlTests
{
    // ─── 基本空間控制 ─────────────────────────────────────────────────────

    [Fact]
    public void SpaceControl_WithMorePiecesInEnemyTerritory_ScoresHigher()
    {
        // 紅方車深入黑方陣地，應有更高空間控制分
        // 紅車在 (1,4)：深入黑方陣地（row 0-4 是黑方半場）
        var boardAdvanced = new Board("4k4/4R4/9/9/9/9/9/9/9/4K4 w - - 0 1");
        // 紅車在 (8,4)：停留在己方陣地（row 5-9 是紅方半場）
        var boardHome = new Board("4k4/9/9/9/9/9/9/9/4R4/4K4 w - - 0 1");

        int advancedScore = SpaceControl.Calculate(boardAdvanced, PieceColor.Red);
        int homeScore = SpaceControl.Calculate(boardHome, PieceColor.Red);

        Assert.True(advancedScore > homeScore,
            $"深入敵陣的空間控制 ({advancedScore}) 應大於停留己方 ({homeScore})");
    }

    [Fact]
    public void SpaceControl_NoAttackingPieces_ReturnsZero()
    {
        // 只有帥將，無攻擊子，空間控制應為 0
        var board = new Board("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        int score = SpaceControl.Calculate(board, PieceColor.Red);

        Assert.Equal(0, score);
    }

    [Fact]
    public void SpaceControl_AlwaysNonNegative()
    {
        // 空間控制分不應為負數
        var boards = new[]
        {
            new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1"),
            new Board("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1"),
            new Board("4k4/4R4/9/9/9/9/9/9/9/4K4 w - - 0 1"),
        };

        foreach (var board in boards)
        {
            int score = SpaceControl.Calculate(board, PieceColor.Red);
            Assert.True(score >= 0, $"空間控制分不應為負數，實際 = {score}");

            score = SpaceControl.Calculate(board, PieceColor.Black);
            Assert.True(score >= 0, $"黑方空間控制分不應為負數，實際 = {score}");
        }
    }

    [Fact]
    public void SpaceControl_DoesNotMutateBoard()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var originalFen = board.ToFen();

        SpaceControl.Calculate(board, PieceColor.Red);
        SpaceControl.Calculate(board, PieceColor.Black);

        Assert.Equal(originalFen, board.ToFen());
    }

    // ─── 評估器整合 ───────────────────────────────────────────────────────

    [Fact]
    public void HandcraftedEvaluator_WithSpaceControl_ReturnsConsistentScore()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var evaluator = new HandcraftedEvaluator();

        int first = evaluator.Evaluate(board);
        int second = evaluator.Evaluate(board);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SpaceControl_MoreControlledSquares_GivesHigherScore()
    {
        // 控制更多格子應得分更高（遞增測試）
        // 一個車 vs 兩個車（兩個車控制更多格）
        var boardOneRook = new Board("4k4/4R4/9/9/9/9/9/9/9/4K4 w - - 0 1");
        var boardTwoRooks = new Board("4k4/4R4/R8/9/9/9/9/9/9/4K4 w - - 0 1");

        int oneScore = SpaceControl.Calculate(boardOneRook, PieceColor.Red);
        int twoScore = SpaceControl.Calculate(boardTwoRooks, PieceColor.Red);

        Assert.True(twoScore > oneScore,
            $"兩車空間控制 ({twoScore}) 應大於一車 ({oneScore})");
    }
}
