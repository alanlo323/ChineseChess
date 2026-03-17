using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Protocol;
using System;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// UcciNotation 座標轉換測試。
///
/// 棋盤索引：index = row * 9 + col
///   row 0 = 黑方底線（rank 9），row 9 = 紅方底線（rank 0）
///   file: 'a' = col 0, 'i' = col 8
///   ucciRank = 9 - boardRow
/// </summary>
public class UcciNotationTests
{
    // ─── IndexToSquare ────────────────────────────────────────────────────

    [Fact]
    public void IndexToSquare_RedKingStart_ShouldReturnE0()
    {
        // 紅帥初始格：row 9, col 4 → index = 9*9+4 = 85, rank = 9-9 = 0 → "e0"
        int index = 85;
        string square = UcciNotation.IndexToSquare(index);
        Assert.Equal("e0", square);
    }

    [Fact]
    public void IndexToSquare_BlackKingStart_ShouldReturnE9()
    {
        // 黑將初始格：row 0, col 4 → index = 0*9+4 = 4, rank = 9-0 = 9 → "e9"
        int index = 4;
        string square = UcciNotation.IndexToSquare(index);
        Assert.Equal("e9", square);
    }

    [Fact]
    public void IndexToSquare_TopLeftCorner_ShouldReturnA9()
    {
        // row 0, col 0 → index = 0, rank = 9 → "a9"
        int index = 0;
        string square = UcciNotation.IndexToSquare(index);
        Assert.Equal("a9", square);
    }

    [Fact]
    public void IndexToSquare_BottomRightCorner_ShouldReturnI0()
    {
        // row 9, col 8 → index = 9*9+8 = 89, rank = 0 → "i0"
        int index = 89;
        string square = UcciNotation.IndexToSquare(index);
        Assert.Equal("i0", square);
    }

    // ─── SquareToIndex ────────────────────────────────────────────────────

    [Fact]
    public void SquareToIndex_A9_ShouldReturnIndex0()
    {
        // "a9" → col=0, rank=9, row=9-9=0, index=0
        int index = UcciNotation.SquareToIndex("a9");
        Assert.Equal(0, index);
    }

    [Fact]
    public void SquareToIndex_E0_ShouldReturnIndex85()
    {
        int index = UcciNotation.SquareToIndex("e0");
        Assert.Equal(85, index);
    }

    [Fact]
    public void SquareToIndex_InvalidString_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => UcciNotation.SquareToIndex("e"));
        Assert.Throws<ArgumentException>(() => UcciNotation.SquareToIndex("z5"));
        Assert.Throws<ArgumentException>(() => UcciNotation.SquareToIndex("eX"));
    }

    // ─── MoveToUcci ───────────────────────────────────────────────────────

    [Fact]
    public void MoveToUcci_KnownMove_ShouldReturnCorrectString()
    {
        // Move(70, 67): from=70 (row7,col7) → "h2", to=67 (row7,col4) → "e2" → "h2e2"
        var move = new Move(70, 67);
        string ucci = UcciNotation.MoveToUcci(move);
        Assert.Equal("h2e2", ucci);
    }

    [Fact]
    public void MoveToUcci_RedKingMove_ShouldReturnCorrectString()
    {
        // 紅帥從 e0(85) 到 d0(84)
        var move = new Move(85, 84);
        string ucci = UcciNotation.MoveToUcci(move);
        Assert.Equal("e0d0", ucci);
    }

    // ─── UcciToMove ───────────────────────────────────────────────────────

    [Fact]
    public void UcciToMove_KnownString_ShouldReturnCorrectMove()
    {
        // "h2e2" → Move(70, 67)
        var move = UcciNotation.UcciToMove("h2e2");
        Assert.Equal(new Move(70, 67), move);
    }

    [Fact]
    public void UcciToMove_InvalidString_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => UcciNotation.UcciToMove("h2e"));   // too short
        Assert.Throws<ArgumentException>(() => UcciNotation.UcciToMove("h2e23")); // too long
        Assert.Throws<ArgumentException>(() => UcciNotation.UcciToMove(null!));    // null
    }

    // ─── Round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_AnyMove_ShouldPreserveOriginal()
    {
        // 挑選幾個典型走法驗證往返一致性
        var testMoves = new[]
        {
            new Move(70, 67),   // h2e2
            new Move(85, 84),   // e0d0
            new Move(0, 9),     // a9a8
            new Move(4, 13),    // e9e8
        };

        foreach (var original in testMoves)
        {
            string ucci = UcciNotation.MoveToUcci(original);
            var restored = UcciNotation.UcciToMove(ucci);
            Assert.Equal(original, restored);
        }
    }
}
