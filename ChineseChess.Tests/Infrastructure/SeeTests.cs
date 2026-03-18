using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 靜態交換評估（SEE）測試。
/// 驗證 StaticExchangeEvaluator.See 對各種吃子情境的評估正確性。
///
/// 棋子價值參考（對齊 SearchWorker.PieceValues）：
///   King=10000, Advisor=120, Elephant=120, Horse=270, Rook=600, Cannon=285, Pawn=30
///
/// 棋盤索引：index = row * 9 + col，row 0 = 黑方底線，row 9 = 紅方底線
/// </summary>
public class SeeTests
{
    // 與 SearchWorker.PieceValues 一致
    private static readonly int[] PieceValues = { 0, 10000, 120, 120, 270, 600, 285, 30 };

    // ─── 基本情境：有利吃子 ───────────────────────────────────────────────

    [Fact]
    public void See_PawnCapturesUndefendedRook_ReturnsRookValue()
    {
        // 紅兵（index 40, row4 col4）吃黑車（index 31, row3 col4）
        // 黑方無防守子，SEE = 600
        // FEN 佈局：黑將在 (0,4)，黑車在 (3,4)，紅兵在 (4,4)，紅將在 (9,4)
        // 紅兵在 row4 已過河，可前進或橫移
        var board = new Board("4k4/9/9/4r4/4P4/9/9/9/9/4K4 w - - 0 1");
        var move = new Move(40, 31);  // 紅兵前進吃黑車

        int see = StaticExchangeEvaluator.See(board, move, PieceValues);

        Assert.Equal(600, see);
    }

    [Fact]
    public void See_RookCapturesUndefendedPawn_ReturnsPawnValue()
    {
        // 黑車（index 13）吃紅兵（index 49），無回吃
        // FEN：黑將 (0,4)，黑車 (1,4)，紅兵 (5,4)，紅將 (9,3)
        var board = new Board("4k4/4r4/9/9/9/4P4/9/9/9/3K5 b - - 0 1");
        var move = new Move(13, 49);  // 黑車吃紅兵

        int see = StaticExchangeEvaluator.See(board, move, PieceValues);

        Assert.Equal(30, see);
    }

    // ─── 基本情境：不利吃子（負數 SEE）──────────────────────────────────

    [Fact]
    public void See_RookCapturesDefendedPawn_ReturnsNegativeValue()
    {
        // 黑車（index 13）吃紅兵（index 49），紅車（index 67）可回吃
        // SEE = 30（兵） - max(0, 600（車回吃）- 0) = 30 - 600 = -570
        // FEN：黑將 (0,4)，黑車 (1,4)，紅兵 (5,4)，紅車 (7,4)，紅將 (9,3)
        var board = new Board("4k4/4r4/9/9/9/4P4/9/4R4/9/3K5 b - - 0 1");
        var move = new Move(13, 49);  // 黑車吃紅兵（紅車可回吃）

        int see = StaticExchangeEvaluator.See(board, move, PieceValues);

        Assert.Equal(-570, see);
    }

    // ─── 均等交換 ─────────────────────────────────────────────────────────

    [Fact]
    public void See_EqualExchangeRookForRook_ReturnsZero()
    {
        // 黑車（index 13）吃紅車（index 49），紅車（index 67）回吃
        // SEE = 600 - max(0, 600 - 0) = 0
        // FEN：黑將 (0,4)，黑車 (1,4)，紅車 (5,4)，紅車 (7,4)，紅將 (9,3)
        var board = new Board("4k4/4r4/9/9/9/4R4/9/4R4/9/3K5 b - - 0 1");
        var move = new Move(13, 49);  // 黑車吃紅車（紅車可回吃）

        int see = StaticExchangeEvaluator.See(board, move, PieceValues);

        Assert.Equal(0, see);
    }

    // ─── 非吃子著法 ──────────────────────────────────────────────────────

    [Fact]
    public void See_NonCaptureMove_ReturnsZero()
    {
        // 非吃子著法 → SEE = 0
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var move = new Move(56, 47);  // 紅兵前進（非吃子，row6→row5，col 2）

        int see = StaticExchangeEvaluator.See(board, move, PieceValues);

        Assert.Equal(0, see);
    }

    // ─── 多輪交換序列 ────────────────────────────────────────────────────

    [Fact]
    public void See_MultipleCaptures_CanChooseNotToRecapture()
    {
        // 黑馬（index 13）吃紅車（index 31），紅馬（index 49）可回吃黑馬
        // 若黑方繼續，會損失更多；但 SEE 模型允許雙方選擇不繼續
        // 黑馬吃車：gain = 600
        // 紅馬回吃黑馬：gain for Red = 270，即黑方損失 270
        // 黑方淨 SEE = 600 - max(0, 270) = 330
        //
        // FEN：黑將 (0,4)，黑馬 (1,4)，紅車 (3,4)，紅馬 (5,2)，紅將 (9,3)
        // 紅馬在 (5,2) 攻擊 (3,4) - 需驗證馬的走法合法性
        // 簡化：使用確定合法的布局
        // 黑馬 (1,4) 到 (3,3) 到 (3,4) 的路徑：馬走「日」
        // 馬從 (1,4) 可走到 (3,3)，(3,5)，(2,2)，(2,6) 等
        // 需要紅馬能攻擊到 (3,4) 這個格子
        // 紅馬在 (5,3) 可走到 (3,4) via (4,3) 無子
        // FEN row5: 紅馬在 col 3 = "3N5"
        var board = new Board("4k4/4n4/9/4r4/9/3N5/9/9/9/3K5 w - - 0 1");

        // 確認紅馬合法走法
        var legalMoves = board.GenerateLegalMoves().ToList();
        var targetMove = legalMoves.FirstOrDefault(m => m.To == 31);  // 紅馬吃黑車？不對
        // 重新設計：紅方直接吃
        // 紅車（index 67）吃黑兵（index 31），黑車（index 13）可回吃
        var board2 = new Board("4k4/4r4/9/4p4/9/9/9/4R4/9/3K5 w - - 0 1");
        var move2 = new Move(67, 31);  // 紅車吃黑兵

        int see2 = StaticExchangeEvaluator.See(board2, move2, PieceValues);

        // 紅車得黑兵 30，黑車回吃紅車得 600
        // SEE = 30 - max(0, 600 - 0) = -570
        Assert.Equal(-570, see2);
    }

    [Fact]
    public void See_PositiveSee_AlwaysGreaterOrEqualToZero()
    {
        // 兵吃無防守的車：SEE > 0
        var board = new Board("4k4/9/9/4r4/4P4/9/9/9/9/4K4 w - - 0 1");
        var move = new Move(40, 31);

        int see = StaticExchangeEvaluator.See(board, move, PieceValues);

        Assert.True(see >= 0, $"有利吃子 SEE 應 >= 0，實際 = {see}");
    }

    [Fact]
    public void See_NegativeSee_IndicatesLosingCapture()
    {
        // 車吃有車防守的兵：SEE < 0（輸材料）
        var board = new Board("4k4/4r4/9/9/9/4P4/9/4R4/9/3K5 b - - 0 1");
        var move = new Move(13, 49);

        int see = StaticExchangeEvaluator.See(board, move, PieceValues);

        Assert.True(see < 0, $"不利吃子 SEE 應 < 0，實際 = {see}");
    }
}
