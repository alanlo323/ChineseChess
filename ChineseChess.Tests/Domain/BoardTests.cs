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

        // Check corner pieces
        Assert.Equal(PieceType.Rook, board.GetPiece(0).Type);
        Assert.Equal(PieceColor.Black, board.GetPiece(0).Color);
        
        Assert.Equal(PieceType.Rook, board.GetPiece(89).Type);
        Assert.Equal(PieceColor.Red, board.GetPiece(89).Color); // 89 is bottom right

        Assert.Equal(PieceColor.Red, board.Turn);
    }

    [Fact]
    public void MakeMove_ShouldUpdateBoardAndTurn()
    {
        var board = new Board();
        board.ParseFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");

        // Move Red Cannon from 77 (1C5C1) to 47 (middle?)
        // Let's pick a valid move index roughly
        // 89 is bottom right. 
        // Row 9: 81-89. Row 7 (Cannons): 63-71. 
        // Red Cannon at 71 (Index 19? No, Red is usually at bottom rows 7-9 in Board array if 0 is top-left?)
        // My ParseFen logic: r=0 is top. r=9 is bottom.
        // Standard FEN: Top is Black. Bottom is Red.
        // So index 0 is Top Left (Black Rook).
        // Index 89 is Bottom Right (Red Rook).
        
        // Red Cannon at Row 7 (index 2 from bottom):
        // r=7 (63-71).
        // 1C5C1 -> 1 empty, C, 5 empty, C, 1 empty.
        // Index 63+1 = 64 (Cannon).
        
        // Move Cannon 64 to 65.
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

        Assert.True(board.GenerateLegalMoves().Any());
    }

    [Fact]
    public void GenerateLegalMoves_ShouldNotAllowMovesLeavingKingInCheck()
    {
        // Red cannon can move out of the blocking column in normal move-generation,
        // but this would expose the red king to the black rook directly above.
        var board = new Board("k3r4/9/9/9/9/4C4/9/9/9/4K4 w - - 0 1");

        var pseudoMoves = board.GeneratePseudoLegalMoves().ToList();
        var illegalMove = new Move(49, 50);

        Assert.True(pseudoMoves.Any(m => m.From == illegalMove.From && m.To == illegalMove.To));
        Assert.False(board.IsCheck(PieceColor.Red));
        Assert.False(board.GenerateLegalMoves().Any(m => m.From == illegalMove.From && m.To == illegalMove.To));
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
        // Black cannon should be able to slide horizontally to an empty square.
        var board = new Board("4k4/9/9/9/9/9/9/4C4/9/4K4 w - - 0 1");
        var move = new Move(67, 68);

        Assert.False(board.IsCheck(PieceColor.Red));

        var pseudoMoves = board.GeneratePseudoLegalMoves().ToList();
        Assert.True(pseudoMoves.Any(m => m.From == move.From && m.To == move.To));
        Assert.True(board.GenerateLegalMoves().Any(m => m.From == move.From && m.To == move.To));
    }

    [Fact]
    public void GeneratePseudoLegalMoves_CannonCannotSlideAfterScreenButCanCaptureOverScreen()
    {
        // Black cannon has a blocking piece at c5 and an enemy piece at c6, so it can capture c6 but
        // cannot move to empty squares beyond the screen.
        var board = new Board("4k4/9/9/9/9/9/9/4cPR2/9/4K4 b - - 0 1");

        var pseudoMoves = board.GeneratePseudoLegalMoves().ToList();
        var legalMoves = board.GenerateLegalMoves().ToList();

        var blockedSlide = new Move(67, 70);
        var legalCapture = new Move(67, 69);

        Assert.True(pseudoMoves.Any(m => m.From == legalCapture.From && m.To == legalCapture.To));
        Assert.False(pseudoMoves.Any(m => m.From == blockedSlide.From && m.To == blockedSlide.To));
        Assert.True(legalMoves.Any(m => m.From == legalCapture.From && m.To == legalCapture.To));
        Assert.False(legalMoves.Any(m => m.From == blockedSlide.From && m.To == blockedSlide.To));
    }
}
