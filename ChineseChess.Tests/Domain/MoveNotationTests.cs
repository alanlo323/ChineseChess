using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// 走法記譜法測試。
/// 棋盤索引：index = row * 9 + col（row 0 = 上方黑方底線，row 9 = 下方紅方底線）。
/// 紅方列號：col 8=1, col 0=9（從右到左）。
/// 黑方列號：col 0=1, col 8=9（從左到右）。
/// </summary>
public class MoveNotationTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── 紅方走法 ─────────────────────────────────────────────────────────

    [Fact]
    public void RedRook_Move_ShouldDisplayAsJu()
    {
        // 紅方俥（Rook）位於 (9,0)=index 81，應顯示「俥」而非「傌」
        var board = new Board(InitialFen);
        Assert.Equal(PieceType.Rook, board.GetPiece(81).Type); // 確認是車

        // 清出路讓車可走：把 (9,1) 的馬移走，讓車向右一格
        // 構造一個車可以走的局面：只放一顆紅車
        var simpleBoard = new Board("9/9/9/9/9/9/9/9/9/R8 w - - 0 1");
        // 紅車在 (9,0)=index 81，往右走到 (9,1)=index 82
        var move = new Move(81, 82);
        var notation = MoveNotation.ToNotation(move, simpleBoard);
        Assert.StartsWith("俥", notation); // 應是「俥」（車），不是「傌」（馬）
    }

    [Fact]
    public void RedHorse_Move_ShouldDisplayAsMa()
    {
        // 紅方傌（Horse）位於 (9,1)=index 82，應顯示「傌」而非「俥」
        var simpleBoard = new Board("9/9/9/9/9/9/9/9/9/1N7 w - - 0 1");
        // 紅馬在 (9,1)=index 82，走馬步到 (7,2)=index 65
        var move = new Move(82, 65);
        var notation = MoveNotation.ToNotation(move, simpleBoard);
        Assert.StartsWith("傌", notation); // 應是「傌」（馬），不是「俥」（車）
    }

    [Fact]
    public void RedRookAdvance_NotationFormat_IsCorrect()
    {
        // 紅俥進一步：俥9進1
        var board = new Board("9/9/9/9/9/9/9/9/R8/9 w - - 0 1");
        // 紅車在 (8,0)=index 72，col0 → 紅方列號 = 9 - 0 = 9
        // 進到 (7,0)=index 63（row 7 < row 8，對紅方是「進」）
        var move = new Move(72, 63);
        var notation = MoveNotation.ToNotation(move, board);
        Assert.Equal("俥9進1", notation);
    }

    [Fact]
    public void BlackRook_Move_ShouldDisplayAsJu()
    {
        // 黑方車（Rook）應顯示「車」而非「馬」
        var simpleBoard = new Board("r8/9/9/9/9/9/9/9/9/9 b - - 0 1");
        // 黑車在 (0,0)=index 0，向右走到 (0,1)=index 1
        var move = new Move(0, 1);
        var notation = MoveNotation.ToNotation(move, simpleBoard);
        Assert.StartsWith("車", notation); // 應是「車」，不是「馬」
    }

    [Fact]
    public void BlackHorse_Move_ShouldDisplayAsMa()
    {
        // 黑方馬（Horse）應顯示「馬」而非「車」
        var simpleBoard = new Board("1n7/9/9/9/9/9/9/9/9/9 b - - 0 1");
        // 黑馬在 (0,1)=index 1，走馬步到 (2,2)=index 20
        var move = new Move(1, 20);
        var notation = MoveNotation.ToNotation(move, simpleBoard);
        Assert.StartsWith("馬", notation); // 應是「馬」，不是「車」
    }

    // ─── 其他棋子名稱正確性 ──────────────────────────────────────────────

    [Fact]
    public void RedKing_ShouldDisplayAsShuai()
    {
        var board = new Board("9/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        var move = new Move(85, 84); // 帥左移
        Assert.StartsWith("帥", MoveNotation.ToNotation(move, board));
    }

    [Fact]
    public void BlackKing_ShouldDisplayAsJiang()
    {
        var board = new Board("4k4/9/9/9/9/9/9/9/9/9 b - - 0 1");
        var move = new Move(4, 5); // 將右移
        Assert.StartsWith("將", MoveNotation.ToNotation(move, board));
    }

    [Fact]
    public void RedCannon_ShouldDisplayAsPao()
    {
        // 紅炮在 (7,1)=index 64，向右平移
        var board = new Board("9/9/9/9/9/9/9/1C7/9/9 w - - 0 1");
        var move = new Move(64, 65);
        Assert.StartsWith("炮", MoveNotation.ToNotation(move, board));
    }

    [Fact]
    public void BlackCannon_ShouldDisplayAsPao()
    {
        var board = new Board("9/1c7/9/9/9/9/9/9/9/9 b - - 0 1");
        var move = new Move(10, 11);
        Assert.StartsWith("砲", MoveNotation.ToNotation(move, board));
    }
}
