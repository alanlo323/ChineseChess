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
}
