using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System.Linq;
using Xunit;

namespace ChineseChess.Tests.Domain;

public class BoardTests
{
    [Fact]
    public void InitialFen_ShouldBeCorrectlyParsed()
    {
        var board = new Board();
        board.ParseFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");

        // 檢查角落棋子
        Assert.Equal(PieceType.Rook, board.GetPiece(0).Type);
        Assert.Equal(PieceColor.Black, board.GetPiece(0).Color);
        
        Assert.Equal(PieceType.Rook, board.GetPiece(89).Type);
        Assert.Equal(PieceColor.Red, board.GetPiece(89).Color); // 89 是右下角

        Assert.Equal(PieceColor.Red, board.Turn);
    }

    [Fact]
    public void MakeMove_ShouldUpdateBoardAndTurn()
    {
        var board = new Board();
        board.ParseFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");

        // 從 77（1C5C1）移動紅方大砲到 47（中段，僅大致示意）。
        // 先挑一個有效的索引
        // 89 是右下角。
        // 第 9 列：81-89，第 7 列（大砲）：63-71。
        // 紅方大砲在 71（索引 19？其實 0 代表左上角時，紅方通常在下半部 7-9 列）。
        // 我的 ParseFen 規則：r=0 在上方，r=9 在下方。
        // 標準 FEN 也是上黑下紅。
        // 所以索引 0 是左上（黑方車）。
        // 索引 89 是右下（紅方車）。
        
        // 重新確認紅方大砲在第 7 列（由下往上第 2 列）：
        // r=7（63-71）。
        // 1C5C1 代表：1 格空位、C、5 格空位、C、1 格空位。
        // 索引 63+1 = 64（大砲）。
        
        // 將大砲從 64 移到 65。
        var move = new Move(64, 65);
        board.MakeMove(move);

        Assert.Equal(PieceType.None, board.GetPiece(64).Type);
        Assert.Equal(PieceType.Cannon, board.GetPiece(65).Type);
        Assert.Equal(PieceColor.Black, board.Turn);
    }

    [Fact]
    public void InitialPosition_GeneratesMoves()
    {
        var board = new Board();
        board.ParseFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");

        Assert.NotEmpty(board.GenerateLegalMoves());
    }

    [Fact]
    public void GenerateLegalMoves_ShouldNotAllowMovesLeavingKingInCheck()
    {
        // 紅方大砲在一般著法產生中可離開阻擋列，
        // 但這會讓紅將暴露在上方黑車的直線攻擊下。
        var board = new Board("k3r4/9/9/9/9/4C4/9/9/9/4K4 w - - 0 1");

        var pseudoMoves = board.GeneratePseudoLegalMoves().ToList();
        var illegalMove = new Move(49, 50);

        Assert.Contains(illegalMove, pseudoMoves);
        Assert.False(board.IsCheck(PieceColor.Red));
        Assert.DoesNotContain(illegalMove, board.GenerateLegalMoves());
    }

    [Fact]
    public void IsCheck_DetectsCheckFromRook()
    {
        var board = new Board("k3r4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");

        Assert.True(board.IsCheck(PieceColor.Red));
        Assert.False(board.IsCheck(PieceColor.Black));
    }

    [Fact]
    public void GenerateLegalMoves_ShouldAllowMoveWhenCannonWouldHaveNoScreen()
    {
        // 紅方大砲應可水平滑到空格（黑將與紅將不同列，避免觸發面將規則）。
        var board = new Board("k8/9/9/9/9/9/9/4C4/9/4K4 w - - 0 1");
        var move = new Move(67, 68);

        Assert.False(board.IsCheck(PieceColor.Red));

        var pseudoMoves = board.GeneratePseudoLegalMoves().ToList();
        Assert.Contains(move, pseudoMoves);
        Assert.Contains(move, board.GenerateLegalMoves());
    }

    [Fact]
    public void GeneratePseudoLegalMoves_CannonCannotSlideAfterScreenButCanCaptureOverScreen()
    {
        // 黑方大砲在 c5 有阻擋棋子、c6 有敵方棋子，因此可吃 c6，
        // 但不能越過阻擋在空格上再繼續移動（黑將與紅將不同列，避免觸發面將規則）。
        var board = new Board("k8/9/9/9/9/9/9/4cPR2/9/4K4 b - - 0 1");

        var pseudoMoves = board.GeneratePseudoLegalMoves().ToList();
        var legalMoves = board.GenerateLegalMoves().ToList();

        var blockedSlide = new Move(67, 70);
        var legalCapture = new Move(67, 69);

        Assert.Contains(legalCapture, pseudoMoves);
        Assert.DoesNotContain(blockedSlide, pseudoMoves);
        Assert.Contains(legalCapture, legalMoves);
        Assert.DoesNotContain(blockedSlide, legalMoves);
    }

    [Fact]
    public void MakeMove_OutOfRangeMove_Throws()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        Assert.Throws<ArgumentOutOfRangeException>(() => board.MakeMove(new Move(-1, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => board.MakeMove(new Move(10, 90)));
    }

    [Fact]
    public void MakeMove_SameSquare_Throws()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        Assert.Throws<InvalidOperationException>(() => board.MakeMove(new Move(64, 64)));
    }

    [Fact]
    public void MakeMove_EmptySquare_Throws()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        Assert.Throws<InvalidOperationException>(() => board.MakeMove(new Move(40, 41)));
    }

    [Fact]
    public void MakeMove_CaptureOwnPiece_Throws()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        Assert.Throws<InvalidOperationException>(() => board.MakeMove(new Move(64, 70)));
    }

    [Fact]
    public void UnmakeMove_MismatchedState_Throws()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var first = new Move(64, 65);
        var second = new Move(19, 20);

        board.MakeMove(first);
        Assert.Throws<InvalidOperationException>(() => board.UnmakeMove(second));
        Assert.True(board.TryGetLastMove(out var top));
        Assert.Equal(first, top);
    }
}
