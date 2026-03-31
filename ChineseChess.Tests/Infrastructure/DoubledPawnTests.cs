using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 PawnStructure 疊兵懲罰（Doubled Pawns Penalty）功能：
///   - 同列有超過 1 個友兵時，每多一個扣 DoubledPawnPenalty 分
///   - 分散在不同列的兵不受懲罰
///   - 懲罰分數隨疊兵數量遞增
///
/// 測試設計原則：
///   - 使用完全孤立的兵（左右列均無友兵），隔離連兵/孤兵效果
///   - 對照組：相同數量的兵分散至不同列
///
/// 棋盤索引：index = row * 9 + col（row 0 = 黑方底線，row 9 = 紅方底線）
/// 紅方兵未過河：row 5-9（己方陣地）；過河：row 0-4
/// </summary>
public class DoubledPawnTests
{
    // ─── 疊兵懲罰基本驗證 ─────────────────────────────────────────────────

    [Fact]
    public void DoubledPawns_TwoInSameColumn_ScoresLowerThanSpread()
    {
        // Board A：兩兵分散在 col 2 和 col 6（均孤立，無疊兵）
        // Board B：兩兵均在 col 4（孤立，且疊兵）
        // 兩個局面的兵均在 row 7（己方後排），均為孤兵
        // Board B 應比 Board A 低 8 分（疊兵懲罰）
        var boardSpread  = new Board("4k4/9/9/9/9/9/9/2P3P2/9/4K4 w - - 0 1");
        var boardDoubled = new Board("4k4/9/9/9/9/9/9/4P4/4P4/4K4 w - - 0 1");

        int scoreSpread  = PawnStructure.Evaluate(boardSpread, PieceColor.Red);
        int scoreDoubled = PawnStructure.Evaluate(boardDoubled, PieceColor.Red);

        Assert.True(scoreDoubled < scoreSpread,
            $"疊兵 ({scoreDoubled}) 應低於分散兵 ({scoreSpread})");
    }

    [Fact]
    public void DoubledPawns_TwoInSameColumn_ExactPenaltyAmount()
    {
        // 兩個完全孤立的兵：一個在 col 2 row 7，一個在 col 6 row 7（均孤立）
        // 對照：兩個完全孤立的兵：col 4 row 7 和 col 4 row 8（均孤立 + 疊兵）
        // 兩種情況孤兵懲罰相同（各 2 個孤兵），唯一差異為疊兵懲罰 -8
        var boardSpread  = new Board("4k4/9/9/9/9/9/9/2P3P2/9/4K4 w - - 0 1");
        var boardDoubled = new Board("4k4/9/9/9/9/9/9/4P4/4P4/4K4 w - - 0 1");

        int scoreSpread  = PawnStructure.Evaluate(boardSpread, PieceColor.Red);
        int scoreDoubled = PawnStructure.Evaluate(boardDoubled, PieceColor.Red);

        int diff = scoreSpread - scoreDoubled;
        Assert.Equal(8, diff);
    }

    [Fact]
    public void DoubledPawns_ThreeInSameColumn_MorePenaltyThanTwo()
    {
        // 三兵在 col 4（rows 5,6,7）vs 兩兵在 col 4（rows 6,7）
        // 三疊兵懲罰 = (3-1)*8 = 16；二疊兵懲罰 = (2-1)*8 = 8
        // 三疊兵比二疊兵多懲罰 8 分
        var boardThree = new Board("4k4/9/9/9/9/4P4/4P4/4P4/9/4K4 w - - 0 1");
        var boardTwo   = new Board("4k4/9/9/9/9/9/4P4/4P4/9/4K4 w - - 0 1");

        int scoreThree = PawnStructure.Evaluate(boardThree, PieceColor.Red);
        int scoreTwo   = PawnStructure.Evaluate(boardTwo, PieceColor.Red);

        Assert.True(scoreThree < scoreTwo,
            $"三疊兵 ({scoreThree}) 應低於二疊兵 ({scoreTwo})");
        // 三疊兵比二疊兵多：孤兵懲罰(-5) + 疊兵懲罰(-8) = -13
        Assert.Equal(scoreTwo - 13, scoreThree);
    }

    // ─── 非疊兵情況不受懲罰 ───────────────────────────────────────────────

    [Fact]
    public void NonDoubledPawns_DifferentColumns_NoPenalty()
    {
        // 每個兵在不同列，不觸發疊兵懲罰
        // 三兵在 col 2, 5, 8（均孤立，相距 3 列）
        var boardSpread = new Board("4k4/9/9/9/9/9/2P2P2P/9/9/4K4 w - - 0 1");

        // 無疊兵：評估分 = 純孤兵懲罰（3 * -5 = -15）
        int score = PawnStructure.Evaluate(boardSpread, PieceColor.Red);

        // 不應有額外的疊兵懲罰（分數應等於純孤兵懲罰）
        Assert.Equal(-15, score);
    }

    // ─── 黑方疊兵也應受懲罰 ──────────────────────────────────────────────

    [Fact]
    public void BlackDoubledPawns_SameColumn_ScoresLowerThanSpread()
    {
        // 黑方兩兵：分散 vs 疊兵
        var boardSpread  = new Board("4k4/9/2p3p2/9/9/9/9/9/9/4K4 b - - 0 1");
        var boardDoubled = new Board("4k4/4p4/4p4/9/9/9/9/9/9/4K4 b - - 0 1");

        int scoreSpread  = PawnStructure.Evaluate(boardSpread, PieceColor.Black);
        int scoreDoubled = PawnStructure.Evaluate(boardDoubled, PieceColor.Black);

        Assert.True(scoreDoubled < scoreSpread,
            $"黑方疊兵 ({scoreDoubled}) 應低於分散兵 ({scoreSpread})");
    }
}
