using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using Xunit;

namespace ChineseChess.Tests.Domain;

public class PieceTests
{
    [Fact]
    public void None_IsNone_IsTrue() => Assert.True(Piece.None.IsNone);

    [Fact]
    public void NonNone_IsNone_IsFalse()
    {
        Assert.False(new Piece(PieceColor.Red, PieceType.King).IsNone);
        Assert.False(new Piece(PieceColor.Black, PieceType.Pawn).IsNone);
    }

    // 驗證 ToChar() 輸出符合標準象棋 FEN 字符（大寫=紅，小寫=黑）
    [Theory]
    [InlineData(PieceColor.Red,   PieceType.King,     'K')]
    [InlineData(PieceColor.Black, PieceType.King,     'k')]
    [InlineData(PieceColor.Red,   PieceType.Advisor,  'A')]
    [InlineData(PieceColor.Black, PieceType.Advisor,  'a')]
    [InlineData(PieceColor.Red,   PieceType.Elephant, 'B')] // 標準 FEN 用 B
    [InlineData(PieceColor.Black, PieceType.Elephant, 'b')]
    [InlineData(PieceColor.Red,   PieceType.Horse,    'N')] // 標準 FEN 用 N
    [InlineData(PieceColor.Black, PieceType.Horse,    'n')]
    [InlineData(PieceColor.Red,   PieceType.Rook,     'R')]
    [InlineData(PieceColor.Black, PieceType.Rook,     'r')]
    [InlineData(PieceColor.Red,   PieceType.Cannon,   'C')]
    [InlineData(PieceColor.Black, PieceType.Cannon,   'c')]
    [InlineData(PieceColor.Red,   PieceType.Pawn,     'P')]
    [InlineData(PieceColor.Black, PieceType.Pawn,     'p')]
    public void ToChar_ReturnsStandardFenChar(PieceColor color, PieceType type, char expected)
        => Assert.Equal(expected, new Piece(color, type).ToChar());

    [Fact]
    public void None_ToChar_ReturnsDot() => Assert.Equal('.', Piece.None.ToChar());

    [Fact]
    public void SamePiece_EqualityOperator_IsTrue()
    {
        var a = new Piece(PieceColor.Red, PieceType.Rook);
        var b = new Piece(PieceColor.Red, PieceType.Rook);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentColor_EqualityOperator_IsFalse()
    {
        var a = new Piece(PieceColor.Red, PieceType.Rook);
        var b = new Piece(PieceColor.Black, PieceType.Rook);
        Assert.True(a != b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DifferentType_EqualityOperator_IsFalse()
    {
        var a = new Piece(PieceColor.Red, PieceType.Rook);
        var b = new Piece(PieceColor.Red, PieceType.Cannon);
        Assert.True(a != b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void NoneEquality_TwiceNone_AreEqual()
    {
        Assert.Equal(Piece.None, Piece.None);
        Assert.True(Piece.None == Piece.None);
    }
}
