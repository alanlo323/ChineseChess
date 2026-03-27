using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Validation;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// 測試 BoardValidator 局面合法性驗證：
/// 1. 紅方帥恰好 1 個（在九宮內）
/// 2. 黑方將恰好 1 個（在九宮內）
/// 3. 將帥不面對面（同列無阻擋）
/// </summary>
public class BoardValidatorTests
{
    // ─── 輔助方法 ────────────────────────────────────────────────────────

    /// <summary>建立只有兩將對峙的最小合法局面（紅帥 col=4,row=9；黑將 col=4,row=0，中間有子阻擋）。</summary>
    private static Board BuildMinimalLegalBoard()
    {
        // 紅帥：row9,col4 → index=85；黑將：row0,col4 → index=4
        // 中間放一個紅方兵在 row4,col4 → index=40 阻擋
        var board = new Board();
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));
        board.SetPiece(4, new Piece(PieceColor.Black, PieceType.King));
        board.SetPiece(40, new Piece(PieceColor.Red, PieceType.Pawn)); // 阻擋飛將
        return board;
    }

    /// <summary>建立兩將對齊但無阻擋（違反「將帥不面對面」規則）的局面。</summary>
    private static Board BuildFlyingKingBoard()
    {
        var board = new Board();
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));   // row9,col4
        board.SetPiece(4, new Piece(PieceColor.Black, PieceType.King));  // row0,col4（同列，無阻擋）
        return board;
    }

    // ─── 合法局面 ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_LegalBoard_ShouldReturnValid()
    {
        var board = BuildMinimalLegalBoard();
        var result = BoardValidator.Validate(board);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_StandardInitialPosition_ShouldReturnValid()
    {
        var board = new Board();
        board.ParseFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var result = BoardValidator.Validate(board);
        Assert.True(result.IsValid);
    }

    // ─── 紅方帥缺失 ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_NoRedKing_ShouldReturnInvalid()
    {
        var board = new Board();
        board.SetPiece(4, new Piece(PieceColor.Black, PieceType.King));
        // 不放紅帥

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("紅方帥"));
    }

    // ─── 黑方將缺失 ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_NoBlackKing_ShouldReturnInvalid()
    {
        var board = new Board();
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));
        // 不放黑將

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("黑方將"));
    }

    // ─── 紅方多於一個帥 ─────────────────────────────────────────────────

    [Fact]
    public void Validate_MultipleRedKings_ShouldReturnInvalid()
    {
        var board = new Board();
        board.SetPiece(4, new Piece(PieceColor.Black, PieceType.King));
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));
        board.SetPiece(84, new Piece(PieceColor.Red, PieceType.King)); // 兩個紅帥

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("紅方帥"));
    }

    // ─── 黑方多於一個將 ─────────────────────────────────────────────────

    [Fact]
    public void Validate_MultipleBlackKings_ShouldReturnInvalid()
    {
        var board = new Board();
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));
        board.SetPiece(3, new Piece(PieceColor.Black, PieceType.King));
        board.SetPiece(5, new Piece(PieceColor.Black, PieceType.King)); // 兩個黑將

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("黑方將"));
    }

    // ─── 帥在九宮外 ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_RedKingOutsidePalace_ShouldReturnInvalid()
    {
        var board = new Board();
        // 九宮外位置：row9,col0 → index=81（col 不在 3-5）
        board.SetPiece(81, new Piece(PieceColor.Red, PieceType.King));
        board.SetPiece(4, new Piece(PieceColor.Black, PieceType.King));

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("紅方帥") && e.Contains("九宮"));
    }

    [Fact]
    public void Validate_BlackKingOutsidePalace_ShouldReturnInvalid()
    {
        var board = new Board();
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));
        // 九宮外位置：row0,col0 → index=0（col 不在 3-5）
        board.SetPiece(0, new Piece(PieceColor.Black, PieceType.King));

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("黑方將") && e.Contains("九宮"));
    }

    // ─── 將帥面對面（飛將） ──────────────────────────────────────────────

    [Fact]
    public void Validate_KingsFacingNoBlocker_ShouldReturnInvalid()
    {
        var board = BuildFlyingKingBoard();

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("面對面") || e.Contains("飛將"));
    }

    [Fact]
    public void Validate_KingsFacingWithBlocker_ShouldReturnValid()
    {
        var board = BuildMinimalLegalBoard(); // 中間有子阻擋

        var result = BoardValidator.Validate(board);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_KingsInDifferentColumns_ShouldNotTriggerFlyingKingRule()
    {
        var board = new Board();
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));    // row9,col4
        board.SetPiece(3, new Piece(PieceColor.Black, PieceType.King));   // row0,col3（不同列）

        var result = BoardValidator.Validate(board);
        Assert.True(result.IsValid); // 不同列，不觸發飛將
    }

    // ─── 多重錯誤 ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_NoKingsAtAll_ShouldReturnMultipleErrors()
    {
        var board = new Board(); // 完全空棋盤

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 2);
    }

    // ─── 九宮邊界精確測試 ───────────────────────────────────────────────

    [Theory]
    [InlineData(66)] // row7,col3 – 九宮左邊界
    [InlineData(68)] // row7,col5 – 九宮右邊界
    [InlineData(84)] // row9,col3 – 九宮左下
    [InlineData(86)] // row9,col5 – 九宮右下
    public void Validate_RedKingInPalaceCorners_ShouldReturnValid(int kingIndex)
    {
        var board = new Board();
        board.SetPiece(kingIndex, new Piece(PieceColor.Red, PieceType.King));
        board.SetPiece(4, new Piece(PieceColor.Black, PieceType.King));   // row0,col4

        // 確認不在同列（避免飛將）
        int redCol = kingIndex % 9;
        int blackCol = 4 % 9; // 4
        if (redCol != blackCol)
        {
            var result = BoardValidator.Validate(board);
            Assert.True(result.IsValid, $"index={kingIndex} 應在九宮內（row={kingIndex / 9}, col={redCol}）");
        }
    }

    [Theory]
    [InlineData(3)]  // row0,col3 – 九宮左邊界
    [InlineData(5)]  // row0,col5 – 九宮右邊界
    [InlineData(21)] // row2,col3 – 九宮左下
    [InlineData(23)] // row2,col5 – 九宮右下
    public void Validate_BlackKingInPalaceCorners_ShouldReturnValid(int kingIndex)
    {
        var board = new Board();
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));   // row9,col4
        board.SetPiece(kingIndex, new Piece(PieceColor.Black, PieceType.King));

        int redCol = 85 % 9; // 4
        int blackCol = kingIndex % 9;
        if (redCol != blackCol)
        {
            var result = BoardValidator.Validate(board);
            Assert.True(result.IsValid, $"index={kingIndex} 應在九宮內（row={kingIndex / 9}, col={blackCol}）");
        }
    }
}
