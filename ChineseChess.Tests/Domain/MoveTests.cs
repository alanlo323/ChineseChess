using ChineseChess.Domain.Entities;
using Xunit;

namespace ChineseChess.Tests.Domain;

public class MoveTests
{
    [Fact]
    public void NullMove_IsNull_IsTrue() => Assert.True(Move.Null.IsNull);

    [Fact]
    public void NullMove_HasZeroFromAndTo()
    {
        Assert.Equal((byte)0, Move.Null.From);
        Assert.Equal((byte)0, Move.Null.To);
    }

    [Fact]
    public void RegularMove_IsNull_IsFalse()
    {
        Assert.False(new Move(0, 1).IsNull);
        Assert.False(new Move(1, 0).IsNull);
        Assert.False(new Move(10, 20).IsNull);
    }

    [Fact]
    public void Properties_StoreFromToAndScore()
    {
        var m = new Move(15, 42, score: 100);
        Assert.Equal((byte)15, m.From);
        Assert.Equal((byte)42, m.To);
        Assert.Equal(100, m.Score);
    }

    [Fact]
    public void Equality_DependsOnlyOnFromTo_NotScore()
    {
        var a = new Move(10, 20, score: 999);
        var b = new Move(10, 20, score: 0);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void Inequality_DifferentFrom()
    {
        var a = new Move(10, 20);
        var b = new Move(11, 20);
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void Inequality_DifferentTo()
    {
        var a = new Move(10, 20);
        var b = new Move(10, 21);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void IntConstructor_SameAsByteConstructor()
    {
        var fromInt  = new Move(30, 60);
        var fromByte = new Move((byte)30, (byte)60);
        Assert.Equal(fromByte, fromInt);
    }

    [Fact]
    public void Hashcode_EqualMoves_SameHashCode()
    {
        var a = new Move(5, 50);
        var b = new Move(5, 50, score: 9999);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
