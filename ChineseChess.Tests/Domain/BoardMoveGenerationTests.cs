using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System.Linq;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// 各棋子走法產生的詳細測試。
/// 棋盤索引：index = row * 9 + col（row 0 = 上方黑方底線，row 9 = 下方紅方底線）。
/// 所有 FEN 使用 k8/... 將黑將置於 (0,0)=0，避免觸發面將規則（除非特別測試面將）。
/// </summary>
public class BoardMoveGenerationTests
{
    // ─── 將（King）────────────────────────────────────────────────

    [Fact]
    public void King_BottomPalaceCenter_Has3Moves()
    {
        // 紅將在 (9,4)=85，宮底中心，可走：(8,4)=76、(9,3)=84、(9,5)=86
        var board = new Board("k8/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        var moves = board.GenerateLegalMoves().ToList();
        Assert.Equal(3, moves.Count);
        Assert.Contains(new Move(85, 76), moves);
        Assert.Contains(new Move(85, 84), moves);
        Assert.Contains(new Move(85, 86), moves);
    }

    [Fact]
    public void King_PalaceTrueCenter_Has4Moves()
    {
        // 紅將在 (8,4)=76（宮心），可走四個方向：67、85、75、77
        var board = new Board("k8/9/9/9/9/9/9/9/4K4/9 w - - 0 1");
        var moves = board.GenerateLegalMoves().ToList();
        Assert.Equal(4, moves.Count);
        Assert.Contains(new Move(76, 67), moves);
        Assert.Contains(new Move(76, 85), moves);
        Assert.Contains(new Move(76, 75), moves);
        Assert.Contains(new Move(76, 77), moves);
    }

    [Fact]
    public void King_PalaceCorner_Has2Moves()
    {
        // 紅將在 (7,3)=66（宮左上角），只能走 (8,3)=75 和 (7,4)=67
        var board = new Board("k8/9/9/9/9/9/9/3K5/9/9 w - - 0 1");
        var moves = board.GenerateLegalMoves().ToList();
        Assert.Equal(2, moves.Count);
        Assert.Contains(new Move(66, 75), moves);
        Assert.Contains(new Move(66, 67), moves);
    }

    [Fact]
    public void King_CannotLeaveOwnPalace()
    {
        // 紅將在 (7,4)=67，不能走到 row 6（宮外）
        var board = new Board("k8/9/9/9/9/9/9/4K4/9/9 w - - 0 1");
        var moves = board.GenerateLegalMoves();
        Assert.DoesNotContain(new Move(67, 58), moves); // (6,4) 在宮外
    }

    [Fact]
    public void King_CanCaptureEnemyInPalace()
    {
        // 紅將在 (9,4)=85，黑方小兵（p）在 (8,4)=76，可吃
        var board = new Board("k8/9/9/9/9/9/9/9/4p4/4K4 w - - 0 1");
        var moves = board.GenerateLegalMoves();
        Assert.Contains(new Move(85, 76), moves);
    }

    // ─── 士（Advisor）─────────────────────────────────────────────

    [Fact]
    public void Advisor_PalaceCenter_Has4DiagonalMoves()
    {
        // 紅士在 (8,4)=76，紅將在 (7,4)=67，四個斜向皆在宮內
        var board = new Board("k8/9/9/9/9/9/9/4K4/4A4/9 w - - 0 1");
        var advisorMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 76)
            .ToList();
        Assert.Equal(4, advisorMoves.Count);
        Assert.Contains(new Move(76, 66), advisorMoves); // (7,3)
        Assert.Contains(new Move(76, 68), advisorMoves); // (7,5)
        Assert.Contains(new Move(76, 84), advisorMoves); // (9,3)
        Assert.Contains(new Move(76, 86), advisorMoves); // (9,5)
    }

    [Fact]
    public void Advisor_PalaceCorner_Has1Move()
    {
        // 紅士在 (9,5)=86，紅將在 (9,4)=85，只剩 (8,4)=76 可走
        var board = new Board("k8/9/9/9/9/9/9/9/9/4KA3 w - - 0 1");
        var advisorMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 86)
            .ToList();
        Assert.Single(advisorMoves);
        Assert.Contains(new Move(86, 76), advisorMoves); // (8,4)
    }

    [Fact]
    public void Advisor_CannotLeaveOwnPalace()
    {
        // 士不能走到宮外（col < 3 或 col > 5，或 row 不在 7-9 之間）
        var board = new Board("k8/9/9/9/9/9/9/9/9/4KA3 w - - 0 1");
        var moves = board.GenerateLegalMoves();
        Assert.DoesNotContain(new Move(86, 78), moves); // (8,6) col=6 宮外
    }

    // ─── 象（Elephant）────────────────────────────────────────────

    [Fact]
    public void Elephant_CenterUnblocked_Has4Moves()
    {
        // 紅象在 (7,4)=67，四個象眼皆空，可走 (5,2)=47、(5,6)=51、(9,2)=83、(9,6)=87
        var board = new Board("k8/9/9/9/9/9/9/4B4/9/4K4 w - - 0 1");
        var elephantMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 67)
            .ToList();
        Assert.Equal(4, elephantMoves.Count);
        Assert.Contains(new Move(67, 47), elephantMoves);
        Assert.Contains(new Move(67, 51), elephantMoves);
        Assert.Contains(new Move(67, 83), elephantMoves);
        Assert.Contains(new Move(67, 87), elephantMoves);
    }

    [Fact]
    public void Elephant_AtRiverEdge_CannotCrossRiver()
    {
        // 紅象在 (5,4)=49（河邊），無法越河走 (3,2) 或 (3,6)
        var board = new Board("k8/9/9/9/9/4B4/9/9/9/4K4 w - - 0 1");
        var elephantMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();
        Assert.Equal(2, elephantMoves.Count); // 只能走 (7,2)=65 和 (7,6)=69
        Assert.Contains(new Move(49, 65), elephantMoves);
        Assert.Contains(new Move(49, 69), elephantMoves);
        Assert.DoesNotContain(new Move(49, 29), elephantMoves); // (3,2) 越河
        Assert.DoesNotContain(new Move(49, 33), elephantMoves); // (3,6) 越河
    }

    [Fact]
    public void Elephant_ElephantEyeBlocked_ReducesMoves()
    {
        // 紅象在 (7,4)=67，象眼 (6,5)=59 有棋子，(-2,+2) 方向被封
        // 其餘三個方向象眼皆空，可走 3 步
        var board = new Board("k8/9/9/9/9/9/5p3/4B4/9/4K4 w - - 0 1");
        var elephantMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 67)
            .ToList();
        Assert.Equal(3, elephantMoves.Count);
        Assert.DoesNotContain(new Move(67, 51), elephantMoves); // (5,6) 被封
        Assert.Contains(new Move(67, 47), elephantMoves);       // (5,2) 通
        Assert.Contains(new Move(67, 83), elephantMoves);       // (9,2) 通
        Assert.Contains(new Move(67, 87), elephantMoves);       // (9,6) 通
    }

    // ─── 馬（Horse）───────────────────────────────────────────────

    [Fact]
    public void Horse_Center_AllLegsOpen_Has8Moves()
    {
        // 紅馬在 (5,4)=49，四條馬腿皆空，可走 8 個目標
        var board = new Board("k8/9/9/9/9/4N4/9/9/9/4K4 w - - 0 1");
        var horseMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();
        Assert.Equal(8, horseMoves.Count);
        Assert.Contains(new Move(49, 68), horseMoves); // (+2,+1) = (7,5)
        Assert.Contains(new Move(49, 66), horseMoves); // (+2,-1) = (7,3)
        Assert.Contains(new Move(49, 32), horseMoves); // (-2,+1) = (3,5)
        Assert.Contains(new Move(49, 30), horseMoves); // (-2,-1) = (3,3)
        Assert.Contains(new Move(49, 60), horseMoves); // (+1,+2) = (6,6)
        Assert.Contains(new Move(49, 56), horseMoves); // (+1,-2) = (6,2)
        Assert.Contains(new Move(49, 42), horseMoves); // (-1,+2) = (4,6)
        Assert.Contains(new Move(49, 38), horseMoves); // (-1,-2) = (4,2)
    }

    [Fact]
    public void Horse_OneVerticalLegBlocked_Reduces2Moves()
    {
        // 紅馬在 (5,4)=49，(6,4)=58 有黑兵（馬腿），(7,5) 和 (7,3) 被封
        var board = new Board("k8/9/9/9/9/4N4/4p4/9/9/4K4 w - - 0 1");
        var horseMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();
        Assert.Equal(6, horseMoves.Count);
        Assert.DoesNotContain(new Move(49, 68), horseMoves); // (7,5) 被封
        Assert.DoesNotContain(new Move(49, 66), horseMoves); // (7,3) 被封
    }

    [Fact]
    public void Horse_EdgePosition_LimitedMoves()
    {
        // 紅馬在 (7,0)=63（邊界），只有 4 個合法目標
        var board = new Board("k8/9/9/9/9/9/9/N8/9/4K4 w - - 0 1");
        var horseMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 63)
            .ToList();
        Assert.Equal(4, horseMoves.Count);
        Assert.Contains(new Move(63, 82), horseMoves); // (+2,+1) = (9,1)
        Assert.Contains(new Move(63, 46), horseMoves); // (-2,+1) = (5,1)
        Assert.Contains(new Move(63, 74), horseMoves); // (+1,+2) = (8,2)
        Assert.Contains(new Move(63, 56), horseMoves); // (-1,+2) = (6,2)
    }

    // ─── 車（Rook）────────────────────────────────────────────────

    [Fact]
    public void Rook_OpenBoard_Has16Slides()
    {
        // 紅車在 (5,4)=49，下方僅被紅將（9,4)=85 阻擋（3 格而非 4 格）
        var board = new Board("k8/9/9/9/9/4R4/9/9/9/4K4 w - - 0 1");
        var rookMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();
        Assert.Equal(16, rookMoves.Count);
        // 向上 5 格
        Assert.Contains(new Move(49, 40), rookMoves);
        Assert.Contains(new Move(49, 31), rookMoves);
        Assert.Contains(new Move(49, 22), rookMoves);
        Assert.Contains(new Move(49, 13), rookMoves);
        Assert.Contains(new Move(49,  4), rookMoves);
        // 向下 3 格（紅將在 row 9 擋住）
        Assert.Contains(new Move(49, 58), rookMoves);
        Assert.Contains(new Move(49, 67), rookMoves);
        Assert.Contains(new Move(49, 76), rookMoves);
        // 向左 4 格
        Assert.Contains(new Move(49, 48), rookMoves);
        Assert.Contains(new Move(49, 47), rookMoves);
        Assert.Contains(new Move(49, 46), rookMoves);
        Assert.Contains(new Move(49, 45), rookMoves);
        // 向右 4 格
        Assert.Contains(new Move(49, 50), rookMoves);
        Assert.Contains(new Move(49, 51), rookMoves);
        Assert.Contains(new Move(49, 52), rookMoves);
        Assert.Contains(new Move(49, 53), rookMoves);
    }

    [Fact]
    public void Rook_BlockedByOwnPiece_StopsBeforePiece()
    {
        // 紅車在 (5,4)=49，同色兵在 (5,2)=47，向左只能走 (5,3)=48
        var board = new Board("k8/9/9/9/9/2P1R4/9/9/9/4K4 w - - 0 1");
        var rookMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();
        Assert.Equal(13, rookMoves.Count);
        Assert.Contains(new Move(49, 48), rookMoves);    // (5,3) 可走
        Assert.DoesNotContain(new Move(49, 47), rookMoves); // (5,2) 是自己的兵，不可走
        Assert.DoesNotContain(new Move(49, 46), rookMoves); // (5,1) 被擋
    }

    [Fact]
    public void Rook_CapturesEnemy_StopsAfterCapture()
    {
        // 紅車在 (5,4)=49，黑兵在 (5,6)=51，向右走 (5,5)=50 和吃 (5,6)=51，不再繼續
        var board = new Board("k8/9/9/9/9/4R1p2/9/9/9/4K4 w - - 0 1");
        var rookMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();
        Assert.Equal(14, rookMoves.Count);
        Assert.Contains(new Move(49, 50), rookMoves);    // (5,5) 空格
        Assert.Contains(new Move(49, 51), rookMoves);    // (5,6) 吃黑兵
        Assert.DoesNotContain(new Move(49, 52), rookMoves); // (5,7) 被吃後無法繼續
    }

    // ─── 砲（Cannon）──────────────────────────────────────────────

    [Fact]
    public void Cannon_EmptyBoard_SlidesOnly_Has16Moves()
    {
        // 紅砲在 (5,4)=49，無敵方棋子，只能滑動（不能架砲吃子）
        var board = new Board("k8/9/9/9/9/4C4/9/9/9/4K4 w - - 0 1");
        var cannonMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();
        Assert.Equal(16, cannonMoves.Count);
    }

    [Fact]
    public void Cannon_CapturesOverExactlyOneScreen()
    {
        // 紅砲在 (5,4)=49，屏障（己方兵）在 (5,6)=51，敵方在 (5,8)=53
        // 可滑到 (5,5)=50，可跨屏吃 (5,8)=53，不能走 (5,7)=52
        var board = new Board("k8/9/9/9/9/4C1P1p/9/9/9/4K4 w - - 0 1");
        var cannonMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();
        Assert.Contains(new Move(49, 50), cannonMoves);    // (5,5) 滑動
        Assert.Contains(new Move(49, 53), cannonMoves);    // (5,8) 跨屏吃子
        Assert.DoesNotContain(new Move(49, 52), cannonMoves); // (5,7) 屏後空格，不可滑
        Assert.DoesNotContain(new Move(49, 51), cannonMoves); // (5,6) 屏障本身，不可走
    }

    [Fact]
    public void Cannon_CannotCaptureOverTwoScreens()
    {
        // 砲不能越過兩個屏障吃子：屏障(5,5)、再一屏(5,6)、敵方在(5,8)
        // 砲在 (5,4)，兩個屏障後的敵子不可吃
        var board = new Board("k8/9/9/9/9/4C1pp1/9/9/9/4K4 w - - 0 1");
        var cannonMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();
        Assert.DoesNotContain(new Move(49, 53), cannonMoves); // (5,8) 兩屏後，不可吃
    }

    [Fact]
    public void Cannon_CannotSlideAfterScreen()
    {
        // 砲遇屏後不能繼續滑動
        var board = new Board("k8/9/9/9/9/4C1P2/9/9/9/4K4 w - - 0 1");
        var cannonMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();
        Assert.Contains(new Move(49, 50), cannonMoves);    // (5,5) 屏前可滑
        Assert.DoesNotContain(new Move(49, 52), cannonMoves); // (5,7) 屏後不可滑
    }

    // ─── 兵（Pawn）────────────────────────────────────────────────

    [Fact]
    public void Pawn_Red_BeforeRiver_ForwardOnly()
    {
        // 紅兵在 (6,4)=58（未過河），只能前進一步到 (5,4)=49
        var board = new Board("k8/9/9/9/9/9/4P4/9/9/4K4 w - - 0 1");
        var pawnMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 58)
            .ToList();
        Assert.Single(pawnMoves);
        Assert.Contains(new Move(58, 49), pawnMoves);
        Assert.DoesNotContain(new Move(58, 57), pawnMoves); // 左側不能走（未過河）
        Assert.DoesNotContain(new Move(58, 59), pawnMoves); // 右側不能走（未過河）
    }

    [Fact]
    public void Pawn_Red_AfterRiver_ThreeMoves()
    {
        // 紅兵在 (3,4)=31（已過河），可前進 (2,4)=22，也可橫走 (3,3)=30 和 (3,5)=32
        var board = new Board("k8/9/9/4P4/9/9/9/9/9/4K4 w - - 0 1");
        var pawnMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 31)
            .ToList();
        Assert.Equal(3, pawnMoves.Count);
        Assert.Contains(new Move(31, 22), pawnMoves); // 前進
        Assert.Contains(new Move(31, 30), pawnMoves); // 橫走左
        Assert.Contains(new Move(31, 32), pawnMoves); // 橫走右
    }

    [Fact]
    public void Pawn_Black_BeforeRiver_ForwardOnly()
    {
        // 黑兵在 (3,4)=31（未過河），只能前進到 (4,4)=40
        // 黑將在 (0,4)=4，紅將在 (9,3)=84（不同列，避免面將）
        var board = new Board("4k4/9/9/4p4/9/9/9/9/9/3K5 b - - 0 1");
        var pawnMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 31)
            .ToList();
        Assert.Single(pawnMoves);
        Assert.Contains(new Move(31, 40), pawnMoves);
    }

    [Fact]
    public void Pawn_Black_AfterRiver_ThreeMoves()
    {
        // 黑兵在 (5,4)=49（已過河），可前進 (6,4)=58，也可橫走 (5,3)=48 和 (5,5)=50
        // 黑將在 (0,0)=0，紅將在 (9,4)=85（不同列）
        var board = new Board("k8/9/9/9/9/4p4/9/9/9/4K4 b - - 0 1");
        var pawnMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();
        Assert.Equal(3, pawnMoves.Count);
        Assert.Contains(new Move(49, 58), pawnMoves); // 前進
        Assert.Contains(new Move(49, 48), pawnMoves); // 橫走左
        Assert.Contains(new Move(49, 50), pawnMoves); // 橫走右
    }

    [Fact]
    public void Pawn_CannotMoveBackward()
    {
        // 紅兵（已過河）不能後退
        var board = new Board("k8/9/9/4P4/9/9/9/9/9/4K4 w - - 0 1");
        var pawnMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 31);
        Assert.DoesNotContain(new Move(31, 40), pawnMoves); // (4,4) 為後退方向
    }
}
