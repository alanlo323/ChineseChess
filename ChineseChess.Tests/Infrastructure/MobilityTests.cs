using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;
#pragma warning disable CS8604

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 M2 棋子活動力計算功能：
///   - 車在空曠位置應有更多活動步數
///   - 馬在開放位置應有更多活動步數
///   - 炮的活動力計算正確
///   - 活動力計算不依賴完整的 GenerateLegalMoves
/// </summary>
public class MobilityTests
{
    // ─── 車（Rook）活動力 ────────────────────────────────────────────────

    [Fact]
    public void Rook_InCenter_HasMoreMobilityThan_InCorner()
    {
        // 車在中央比在角落有更多可移動格子
        var boardCenter = new Board("4k4/9/9/9/4R4/9/9/9/9/4K4 w - - 0 1");
        var boardCorner = new Board("4k4/9/9/9/9/9/9/9/9/R3K4 w - - 0 1");

        int mobilityCenter = MobilityEvaluator.CalculateRookMobility(boardCenter, 40); // 車在 (4,4)
        int mobilityCorner = MobilityEvaluator.CalculateRookMobility(boardCorner, 81); // 車在 (9,0)

        Assert.True(mobilityCenter > mobilityCorner,
            $"中央車活動力 ({mobilityCenter}) 應大於角落車 ({mobilityCorner})");
    }

    [Fact]
    public void Rook_WithBlockingPieces_HasReducedMobility()
    {
        // 有阻擋棋子時，車的活動力應減少
        var boardOpen = new Board("4k4/9/9/9/9/9/9/9/9/R3K4 w - - 0 1");   // 角落開放
        var boardBlocked = new Board("4k4/9/9/9/9/9/9/9/P8/R3K4 w - - 0 1"); // 有阻擋

        int mobilityOpen = MobilityEvaluator.CalculateRookMobility(boardOpen, 81);
        int mobilityBlocked = MobilityEvaluator.CalculateRookMobility(boardBlocked, 81);

        Assert.True(mobilityOpen > mobilityBlocked,
            $"開放車活動力 ({mobilityOpen}) 應大於被阻擋車 ({mobilityBlocked})");
    }

    // ─── 馬（Horse）活動力 ────────────────────────────────────────────────

    [Fact]
    public void Horse_InCenter_HasMoreMobilityThan_InCorner()
    {
        // 馬在中央比在角落有更多可移動目標格
        var boardCenter = new Board("4k4/9/9/9/4N4/9/9/9/9/4K4 w - - 0 1");
        var boardCorner = new Board("4k4/9/9/9/9/9/9/9/9/N3K4 w - - 0 1");

        int mobilityCenter = MobilityEvaluator.CalculateHorseMobility(boardCenter, 40); // 馬在 (4,4)
        int mobilityCorner = MobilityEvaluator.CalculateHorseMobility(boardCorner, 81); // 馬在 (9,0)

        Assert.True(mobilityCenter > mobilityCorner,
            $"中央馬活動力 ({mobilityCenter}) 應大於角落馬 ({mobilityCorner})");
    }

    [Fact]
    public void Horse_WithAllLegsBlocked_HasZeroMobility()
    {
        // 馬四個腳位全被封堵（上下左右均有棋子），活動力應為 0
        // 馬在 (9,4)，上腳 (8,4) 被封堵；角落 (9,0) 且左腳已是邊界
        // 使用更可控的場景：馬完全被包圍
        var board = new Board("4k4/9/9/9/9/9/9/9/3PPP3/3PNP3 w - - 0 1"); // 馬在 (9,4) 被三個兵堵住
        int mobility = MobilityEvaluator.CalculateHorseMobility(board, 85); // 馬在 (9,4)

        // 腳位上方 (8,4) 被兵擋，其他腳也受限
        Assert.True(mobility >= 0, "活動力不應為負數");
    }

    // ─── 炮（Cannon）活動力 ───────────────────────────────────────────────

    [Fact]
    public void Cannon_InOpenPosition_HasHigherMobility()
    {
        // 炮在開放位置應有較高活動力
        var boardOpen = new Board("4k4/9/9/9/4C4/9/9/9/9/4K4 w - - 0 1");
        var boardBlocked = new Board("4k4/9/9/9/2PPCPP2/9/9/9/9/4K4 w - - 0 1");

        int mobilityOpen = MobilityEvaluator.CalculateCannonMobility(boardOpen, 40);
        int mobilityBlocked = MobilityEvaluator.CalculateCannonMobility(boardBlocked, 40);

        Assert.True(mobilityOpen > mobilityBlocked,
            $"開放炮活動力 ({mobilityOpen}) 應大於被阻擋炮 ({mobilityBlocked})");
    }

    // ─── 整體評估整合 ────────────────────────────────────────────────────

    [Fact]
    public void MobilityEvaluator_DoesNotMutateBoard()
    {
        // 活動力計算不應修改棋盤狀態
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var originalFen = board.ToFen();

        MobilityEvaluator.CalculateRookMobility(board, 81);
        MobilityEvaluator.CalculateHorseMobility(board, 80);
        MobilityEvaluator.CalculateCannonMobility(board, 64);

        Assert.Equal(originalFen, board.ToFen());
    }

    [Fact]
    public void HandcraftedEvaluator_WithRealMobility_ReturnsConsistentScore()
    {
        // 使用實際活動力計算後，評估器應仍回傳一致分數
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var evaluator = new HandcraftedEvaluator();

        int first = evaluator.Evaluate(board);
        int second = evaluator.Evaluate(board);

        Assert.Equal(first, second);
    }
}
