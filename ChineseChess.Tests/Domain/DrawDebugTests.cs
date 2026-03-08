using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using Xunit;
using System.Linq;

namespace ChineseChess.Tests.Domain;
public class DrawDebugTests
{
    [Fact]
    public void Debug_LoopFenMoveGeneration()
    {
        const string LoopFen = "4ka3/9/9/9/9/9/9/9/9/R2K5 w - - 0 1";
        var board = new Board(LoopFen);
        
        Assert.Equal(PieceColor.Red, board.Turn);
        Assert.Equal(PieceType.Rook, board.GetPiece(81).Type);
        Assert.Equal(PieceColor.Red, board.GetPiece(81).Color);
        Assert.True(board.GetPiece(72).IsNone);  // a2 should be empty
        Assert.Equal(PieceType.Advisor, board.GetPiece(5).Type);  // f10
        Assert.Equal(PieceColor.Black, board.GetPiece(5).Color);
        
        var moves = board.GenerateLegalMoves().ToList();
        var move81to72 = moves.Any(m => m.From == 81 && m.To == 72);
        Assert.True(move81to72, $"Move 81->72 not found. Legal moves: {string.Join(", ", moves.Select(m => $"{m.From}->{m.To}"))}");
    }
}
