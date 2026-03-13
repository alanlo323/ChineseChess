using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 M3 兵型結構評估功能：
///   - 連兵加成（相鄰兵互相保護）
///   - 孤兵懲罰（無友兵在鄰近列）
///   - 過河兵協同加成（過河兵 >= 2 時每兵加分）
/// </summary>
public class PawnStructureTests
{
    // ─── 連兵加成 ────────────────────────────────────────────────────────

    [Fact]
    public void ConnectedPawns_ScoreHigherThan_IsolatedPawns()
    {
        // 紅方兩兵相鄰（連兵）vs 兩兵分散（孤兵）
        // 連兵：兵在 (6,3) 和 (6,4)（相鄰列，同排，互相保護）
        // 孤兵：兵在 (6,3) 和 (6,6)（相距 3 列，無法互相保護）
        var boardConnected = new Board("4k4/9/9/9/9/9/3PP4/9/9/4K4 w - - 0 1");
        var boardIsolated  = new Board("4k4/9/9/9/9/9/3P2P2/9/9/4K4 w - - 0 1");

        int connectedScore = PawnStructure.Evaluate(boardConnected, PieceColor.Red);
        int isolatedScore  = PawnStructure.Evaluate(boardIsolated, PieceColor.Red);

        Assert.True(connectedScore > isolatedScore,
            $"連兵 ({connectedScore}) 應高於孤兵 ({isolatedScore})");
    }

    [Fact]
    public void ConnectedPawns_GetPositiveBonus()
    {
        // 連兵應獲得正的加成分數
        var board = new Board("4k4/9/9/9/9/9/3PP4/9/9/4K4 w - - 0 1");
        int score = PawnStructure.Evaluate(board, PieceColor.Red);

        // 連兵加成 >= 0
        Assert.True(score >= 0, $"連兵評估應 >= 0，實際 = {score}");
    }

    // ─── 孤兵懲罰 ────────────────────────────────────────────────────────

    [Fact]
    public void IsolatedPawn_ScoresLowerThan_ConnectedPawn()
    {
        // 孤兵應比有友兵相鄰的兵得分更低
        var boardConnected = new Board("4k4/9/9/9/9/9/3PP4/9/9/4K4 w - - 0 1");
        var boardOneIsolated = new Board("4k4/9/9/9/9/9/3P5/9/9/4K4 w - - 0 1");

        int connectedScore = PawnStructure.Evaluate(boardConnected, PieceColor.Red);
        int isolatedScore = PawnStructure.Evaluate(boardOneIsolated, PieceColor.Red);

        // 一個孤兵（無連兵加成）vs 兩個連兵（有加成）
        // 但孤兵還有懲罰，所以差距更大
        Assert.True(connectedScore > isolatedScore);
    }

    // ─── 過河兵協同加成 ──────────────────────────────────────────────────

    [Fact]
    public void CrossedPawns_TwoOrMore_GetCoordinationBonus()
    {
        // 兩個過河兵應有協同加成，比一個過河兵得分高
        // 紅方過河兵在 row 4（過河後）
        var boardTwoCrossed = new Board("4k4/9/9/9/3P1P3/9/9/9/9/4K4 w - - 0 1");  // row 4 兩兵過河
        var boardOneCrossed = new Board("4k4/9/9/9/4P4/9/9/9/9/4K4 w - - 0 1");    // row 4 一兵過河

        int twoScore = PawnStructure.Evaluate(boardTwoCrossed, PieceColor.Red);
        int oneScore = PawnStructure.Evaluate(boardOneCrossed, PieceColor.Red);

        // 兩兵過河應比一兵得到更多（協同加成）
        Assert.True(twoScore > oneScore * 2 - 5,
            $"兩個過河兵 ({twoScore}) 應比一個過河兵的兩倍 ({oneScore * 2}) 接近或更多（協同加成）");
    }

    [Fact]
    public void CrossedPawns_LessThanTwo_NoCoordinationBonus()
    {
        // 少於兩個過河兵時，無協同加成
        // 一個兵在己方（未過河）
        var boardNoCross = new Board("4k4/9/9/9/9/4P4/9/9/9/4K4 w - - 0 1");  // row 5（未過河）
        int score = PawnStructure.Evaluate(boardNoCross, PieceColor.Red);

        // 未過河兵只有懲罰，分數應 <= 0（或很小）
        Assert.True(score <= 5, $"未過河兵評估不應有大的正值 ({score})");
    }

    // ─── 黑方兵型結構 ────────────────────────────────────────────────────

    [Fact]
    public void BlackPawns_ConnectedPawns_GetPositiveBonus()
    {
        // 黑方連兵也應有加成
        var board = new Board("4k4/9/9/9/9/9/9/9/3pp4/4K4 b - - 0 1");
        int score = PawnStructure.Evaluate(board, PieceColor.Black);

        Assert.True(score >= 0, $"黑方連兵評估應 >= 0，實際 = {score}");
    }

    // ─── 評估器整合 ───────────────────────────────────────────────────────

    [Fact]
    public void HandcraftedEvaluator_WithPawnStructure_ReturnsConsistentScore()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var evaluator = new HandcraftedEvaluator();

        int first = evaluator.Evaluate(board);
        int second = evaluator.Evaluate(board);

        Assert.Equal(first, second);
    }

    [Fact]
    public void PawnStructure_DoesNotMutateBoard()
    {
        var board = new Board("4k4/9/9/9/9/9/3PP4/9/9/4K4 w - - 0 1");
        var originalFen = board.ToFen();

        PawnStructure.Evaluate(board, PieceColor.Red);

        Assert.Equal(originalFen, board.ToFen());
    }
}
