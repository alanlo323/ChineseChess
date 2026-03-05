using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System.Linq;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// 將軍偵測、將死、困斃的完整測試。
/// 棋盤索引：index = row * 9 + col（row 0 = 上方黑方底線，row 9 = 下方紅方底線）。
/// </summary>
public class BoardCheckTests
{
    // ─── IsCheck：各棋子將軍偵測 ──────────────────────────────────

    [Fact]
    public void IsCheck_Rook_DirectFile_ReturnsTrue()
    {
        // 黑車在 (8,4)=76，紅將在 (9,4)=85，同列相鄰 → 紅方被將
        var board = new Board("k8/9/9/9/9/9/9/9/4r4/4K4 w - - 0 1");
        Assert.True(board.IsCheck(PieceColor.Red));
        Assert.False(board.IsCheck(PieceColor.Black));
    }

    [Fact]
    public void IsCheck_Rook_DirectRank_ReturnsTrue()
    {
        // 黑車在 (9,0)=81，紅將在 (9,4)=85，同行且無阻擋 → 紅方被將
        var board = new Board("k8/9/9/9/9/9/9/9/9/r3K4 w - - 0 1");
        Assert.True(board.IsCheck(PieceColor.Red));
    }

    [Fact]
    public void IsCheck_Rook_BlockedByPiece_ReturnsFalse()
    {
        // 黑車在 (8,4)=76，中間有棋子在 (8,5)=77，紅將在 (9,4)=85 未被將
        // 等等，這裡應測同行/列有阻擋：黑車在 (7,4)=67，紅兵在 (8,4)=76，紅將在 (9,4)=85
        var board = new Board("k8/9/9/9/9/9/9/4r4/4P4/4K4 w - - 0 1");
        Assert.False(board.IsCheck(PieceColor.Red));
    }

    [Fact]
    public void IsCheck_Cannon_WithOneScreen_ReturnsTrue()
    {
        // 黑砲在 (7,4)=67，屏障（紅兵）在 (8,4)=76，紅將在 (9,4)=85 → 被砲將
        var board = new Board("k8/9/9/9/9/9/9/4c4/4P4/4K4 w - - 0 1");
        Assert.True(board.IsCheck(PieceColor.Red));
    }

    [Fact]
    public void IsCheck_Cannon_WithoutScreen_ReturnsFalse()
    {
        // 黑砲在 (7,4)=67，中間無屏障，紅將在 (9,4)=85 → 未被砲將（砲需屏障才能吃子）
        var board = new Board("k8/9/9/9/9/9/9/4c4/9/4K4 w - - 0 1");
        Assert.False(board.IsCheck(PieceColor.Red));
    }

    [Fact]
    public void IsCheck_Cannon_TwoScreens_ReturnsFalse()
    {
        // 黑砲在 (6,4)=58，兩個屏障 (7,4)=67 和 (8,4)=76，紅將在 (9,4)=85 → 未被砲將
        var board = new Board("k8/9/9/9/9/9/4c4/4P4/4P4/4K4 w - - 0 1");
        Assert.False(board.IsCheck(PieceColor.Red));
    }

    [Fact]
    public void IsCheck_Horse_AttacksKing_ReturnsTrue()
    {
        // 黑馬在 (7,5)=68，紅將在 (9,4)=85：方向 (+2,-1)，馬腿 (8,5)=77 空 → 將軍
        var board = new Board("k8/9/9/9/9/9/9/5n3/9/4K4 w - - 0 1");
        Assert.True(board.IsCheck(PieceColor.Red));
    }

    [Fact]
    public void IsCheck_Horse_LegBlocked_ReturnsFalse()
    {
        // 黑馬在 (7,5)=68，馬腿 (8,5)=77 有紅兵 → 馬腿被封，不將軍
        var board = new Board("k8/9/9/9/9/9/9/5n3/5P3/4K4 w - - 0 1");
        Assert.False(board.IsCheck(PieceColor.Red));
    }

    [Fact]
    public void IsCheck_Pawn_Forward_ReturnsTrue()
    {
        // 黑兵在 (8,4)=76（未過河），紅將在 (9,4)=85：黑兵前進方向 (+1,0) → 將軍
        var board = new Board("k8/9/9/9/9/9/9/9/4p4/4K4 w - - 0 1");
        Assert.True(board.IsCheck(PieceColor.Red));
    }

    [Fact]
    public void IsCheck_Pawn_AfterRiver_SidewaysAttack_ReturnsTrue()
    {
        // 黑兵在 (5,5)=50（已過河），向側攻擊 (5,4)=49... 不過紅將在 (9,4)。
        // 改測：黑兵在 (9,5)=86，紅將在 (9,4)=85，黑兵過河後可橫走 → 將軍（row 9 >= 5）
        var board = new Board("k8/9/9/9/9/9/9/9/9/4Kp3 w - - 0 1");
        Assert.True(board.IsCheck(PieceColor.Red));
    }

    [Fact]
    public void IsCheck_Advisor_CannotAttackDistantly()
    {
        // 士只能走斜一格，且不離宮，所以不可能從宮內將對方（通常難到達）
        // 驗證：黑士在 (1,3)=12（宮內），紅將在 (9,4)=85 → 未被將
        var board = new Board("5k3/3a5/9/9/9/9/9/9/9/4K4 w - - 0 1");
        Assert.False(board.IsCheck(PieceColor.Red));
    }

    // ─── 面將規則（飛將）──────────────────────────────────────────

    [Fact]
    public void IsCheck_FlyingGeneral_SameColumnNoBlocker_ReturnsTrue()
    {
        // 雙將同列（col 4），中間無棋子 → 雙方皆被將
        var board = new Board("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        Assert.True(board.IsCheck(PieceColor.Red));
        Assert.True(board.IsCheck(PieceColor.Black));
    }

    [Fact]
    public void IsCheck_FlyingGeneral_BlockedByPiece_ReturnsFalse()
    {
        // 雙將同列，中間有棋子（紅兵在 (5,4)=49）阻擋 → 不被面將
        var board = new Board("4k4/9/9/9/9/4P4/9/9/9/4K4 w - - 0 1");
        Assert.False(board.IsCheck(PieceColor.Red));
        Assert.False(board.IsCheck(PieceColor.Black));
    }

    [Fact]
    public void IsCheck_DifferentColumns_NoFlyingGeneral()
    {
        // 雙將不同列，不觸發面將
        var board = new Board("k8/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        Assert.False(board.IsCheck(PieceColor.Red));
        Assert.False(board.IsCheck(PieceColor.Black));
    }

    // ─── GenerateLegalMoves 過濾將軍走法 ──────────────────────────

    [Fact]
    public void LegalMoves_DoNotLeaveKingInCheck()
    {
        // 紅將在 (9,4)=85，黑車在 (0,4)=4 瞄準同列（面將透過炮移動暴露）
        // 紅砲在 (5,4)=49 阻擋，移走紅砲（例如橫移）後紅將暴露 → 非法
        var board = new Board("k3r4/9/9/9/9/4C4/9/9/9/4K4 w - - 0 1");
        var illegalMove = new Move(49, 50); // 砲橫移，暴露紅將
        Assert.DoesNotContain(illegalMove, board.GenerateLegalMoves());
    }

    // ─── 將死（Checkmate）─────────────────────────────────────────

    [Fact]
    public void IsCheckmate_TwoRooksBackRank_ReturnsTrue()
    {
        // 紅將在 (9,4)=85，黑車在 (8,0)=72 控制第 8 行，黑車在 (9,8)=89 控制第 9 行
        // 紅將無路可走，且被將 → 將死
        var board = new Board("k8/9/9/9/9/9/9/9/r8/4K3r w - - 0 1");
        Assert.True(board.IsCheck(PieceColor.Red));
        Assert.Empty(board.GenerateLegalMoves());
        Assert.True(board.IsCheckmate(PieceColor.Red));
    }

    [Fact]
    public void IsCheckmate_NotInCheck_ReturnsFalse()
    {
        // 初始局面，未被將，不是將死
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        Assert.False(board.IsCheckmate(PieceColor.Red));
        Assert.False(board.IsCheckmate(PieceColor.Black));
    }

    [Fact]
    public void IsCheckmate_InCheckButHasEscape_ReturnsFalse()
    {
        // 黑車在 (8,4)=76 將紅將，但紅將可移到 (9,3) 或 (9,5) 逃脫
        var board = new Board("k8/9/9/9/9/9/9/9/4r4/4K4 w - - 0 1");
        Assert.True(board.IsCheck(PieceColor.Red));
        Assert.False(board.IsCheckmate(PieceColor.Red));
    }

    // ─── 困斃（Stalemate）─────────────────────────────────────────

    [Fact]
    public void Stalemate_BlackKingTrapped_NoLegalMoves_NotInCheck()
    {
        // 黑將在 (0,4)=4，紅兵在 (1,3)=12 和 (1,5)=14 封死三格
        // 紅將在 (9,3)=84（不同列，避免面將）
        // 黑方輪走但無合法著法，且不被將 → 困斃
        var board = new Board("4k4/3P1P3/9/9/9/9/9/9/9/3K5 b - - 0 1");
        Assert.False(board.IsCheck(PieceColor.Black));
        Assert.Empty(board.GenerateLegalMoves());
    }

    [Fact]
    public void Stalemate_IsNot_Checkmate()
    {
        // 困斃（不被將但無路可走）不等於將死
        var board = new Board("4k4/3P1P3/9/9/9/9/9/9/9/3K5 b - - 0 1");
        Assert.False(board.IsCheckmate(PieceColor.Black));
    }

    [Fact]
    public void Checkmate_IsAlso_InCheck()
    {
        // 將死必然處於被將狀態
        var board = new Board("k8/9/9/9/9/9/9/9/r8/4K3r w - - 0 1");
        Assert.True(board.IsCheck(PieceColor.Red));
        Assert.True(board.IsCheckmate(PieceColor.Red));
    }
}
