using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Validation;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// 測試 BoardValidator 局面合法性驗證：
/// 1. 紅方帥恰好 1 個（在九宮內）
/// 2. 黑方將恰好 1 個（在九宮內）
/// 3. 仕/士位置合法性
/// 4. 相/象位置合法性
/// 5. 兵/卒位置合法性
/// 6. 各棋子數量上限
/// 7. 將帥不面對面（同列無阻擋）
/// 8. 單棋子放置驗證 ValidatePlacement
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
        Assert.Contains(result.Errors, e => e.Contains("紅方帥"));
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
        Assert.Contains(result.Errors, e => e.Contains("黑方將"));
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

    // ═══ 仕/士位置驗證 ═══════════════════════════════════════════════════

    [Theory]
    [InlineData(66)] // (7,3) 紅仕九宮左上
    [InlineData(68)] // (7,5) 紅仕九宮右上
    [InlineData(76)] // (8,4) 紅仕九宮中心
    [InlineData(84)] // (9,3) 紅仕九宮左下
    [InlineData(86)] // (9,5) 紅仕九宮右下
    public void ValidatePlacement_RedAdvisorAtValidPosition_ShouldReturnNull(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Red, PieceType.Advisor));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(75)] // (8,3) 九宮內但非對角線
    [InlineData(77)] // (8,5) 九宮內但非對角線
    [InlineData(67)] // (7,4) 九宮內上方中心
    [InlineData(85)] // (9,4) 九宮內下方中心
    [InlineData(0)]  // 完全在九宮外
    [InlineData(45)] // 棋盤中間
    public void ValidatePlacement_RedAdvisorAtInvalidPosition_ShouldReturnError(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Red, PieceType.Advisor));
        Assert.NotNull(result);
        Assert.Contains("紅仕", result);
    }

    [Theory]
    [InlineData(3)]  // (0,3) 黑士九宮左上
    [InlineData(5)]  // (0,5) 黑士九宮右上
    [InlineData(13)] // (1,4) 黑士九宮中心
    [InlineData(21)] // (2,3) 黑士九宮左下
    [InlineData(23)] // (2,5) 黑士九宮右下
    public void ValidatePlacement_BlackAdvisorAtValidPosition_ShouldReturnNull(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Black, PieceType.Advisor));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(4)]  // (0,4) 九宮內上方中心（非對角線）
    [InlineData(12)] // (1,3) 九宮內但非對角線
    [InlineData(14)] // (1,5) 九宮內但非對角線
    [InlineData(22)] // (2,4) 九宮內下方中心
    [InlineData(50)] // 棋盤中間
    public void ValidatePlacement_BlackAdvisorAtInvalidPosition_ShouldReturnError(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Black, PieceType.Advisor));
        Assert.NotNull(result);
        Assert.Contains("黑士", result);
    }

    // ═══ 相/象位置驗證 ═══════════════════════════════════════════════════

    [Theory]
    [InlineData(47)] // (5,2)
    [InlineData(51)] // (5,6)
    [InlineData(63)] // (7,0)
    [InlineData(67)] // (7,4)
    [InlineData(71)] // (7,8)
    [InlineData(83)] // (9,2)
    [InlineData(87)] // (9,6)
    public void ValidatePlacement_RedElephantAtValidPosition_ShouldReturnNull(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Red, PieceType.Elephant));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(2)]  // (0,2) 黑方半場
    [InlineData(6)]  // (0,6) 黑方半場
    [InlineData(45)] // (5,0) 己方半場但非田字頂點
    [InlineData(50)] // (5,5) 己方半場但非田字頂點
    public void ValidatePlacement_RedElephantAtInvalidPosition_ShouldReturnError(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Red, PieceType.Elephant));
        Assert.NotNull(result);
        Assert.Contains("紅相", result);
    }

    [Theory]
    [InlineData(2)]  // (0,2)
    [InlineData(6)]  // (0,6)
    [InlineData(18)] // (2,0)
    [InlineData(22)] // (2,4)
    [InlineData(26)] // (2,8)
    [InlineData(38)] // (4,2)
    [InlineData(42)] // (4,6)
    public void ValidatePlacement_BlackElephantAtValidPosition_ShouldReturnNull(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Black, PieceType.Elephant));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(47)] // (5,2) 紅方半場（過河）
    [InlineData(51)] // (5,6) 紅方半場（過河）
    [InlineData(0)]  // (0,0) 己方半場但非田字頂點
    public void ValidatePlacement_BlackElephantAtInvalidPosition_ShouldReturnError(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Black, PieceType.Elephant));
        Assert.NotNull(result);
        Assert.Contains("黑象", result);
    }

    // ═══ 兵/卒位置驗證 ═══════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]  // row 0（已過河最前線）
    [InlineData(36)] // row 4（即將過河）
    [InlineData(54)] // row 6（起始行）
    public void ValidatePlacement_RedPawnAtValidPosition_ShouldReturnNull(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Red, PieceType.Pawn));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(63)] // row 7（底線後方）
    [InlineData(72)] // row 8
    [InlineData(81)] // row 9
    public void ValidatePlacement_RedPawnAtBackRows_ShouldReturnError(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Red, PieceType.Pawn));
        Assert.NotNull(result);
        Assert.Contains("紅兵", result);
    }

    [Theory]
    [InlineData(27)] // row 3（起始行）
    [InlineData(45)] // row 5（已過河）
    [InlineData(81)] // row 9（最遠）
    public void ValidatePlacement_BlackPawnAtValidPosition_ShouldReturnNull(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Black, PieceType.Pawn));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0)]  // row 0（底線後方）
    [InlineData(9)]  // row 1
    [InlineData(18)] // row 2
    public void ValidatePlacement_BlackPawnAtBackRows_ShouldReturnError(int index)
    {
        var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Black, PieceType.Pawn));
        Assert.NotNull(result);
        Assert.Contains("黑卒", result);
    }

    // ═══ 車/馬/炮可在任意位置 ════════════════════════════════════════════

    [Theory]
    [InlineData(PieceType.Rook)]
    [InlineData(PieceType.Horse)]
    [InlineData(PieceType.Cannon)]
    public void ValidatePlacement_RookHorseCannon_AnyPosition_ShouldReturnNull(PieceType type)
    {
        // 測試四個角 + 中心
        foreach (var index in new[] { 0, 8, 81, 89, 40 })
        {
            var result = BoardValidator.ValidatePlacement(index, new Piece(PieceColor.Red, type));
            Assert.Null(result);
        }
    }

    // ═══ 數量超限驗證（全盤 Validate） ═══════════════════════════════════

    [Fact]
    public void Validate_TooManyRedAdvisors_ShouldReturnInvalid()
    {
        var board = BuildMinimalLegalBoard();
        board.SetPiece(66, new Piece(PieceColor.Red, PieceType.Advisor));
        board.SetPiece(68, new Piece(PieceColor.Red, PieceType.Advisor));
        board.SetPiece(76, new Piece(PieceColor.Red, PieceType.Advisor)); // 第 3 個

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("紅仕") && e.Contains("超出上限"));
    }

    [Fact]
    public void Validate_TooManyBlackPawns_ShouldReturnInvalid()
    {
        var board = BuildMinimalLegalBoard();
        // 放 6 個黑卒（上限 5）
        board.SetPiece(27, new Piece(PieceColor.Black, PieceType.Pawn));
        board.SetPiece(29, new Piece(PieceColor.Black, PieceType.Pawn));
        board.SetPiece(31, new Piece(PieceColor.Black, PieceType.Pawn));
        board.SetPiece(33, new Piece(PieceColor.Black, PieceType.Pawn));
        board.SetPiece(35, new Piece(PieceColor.Black, PieceType.Pawn));
        board.SetPiece(45, new Piece(PieceColor.Black, PieceType.Pawn)); // 第 6 個

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("黑卒") && e.Contains("超出上限"));
    }

    [Fact]
    public void Validate_TooManyRedRooks_ShouldReturnInvalid()
    {
        var board = BuildMinimalLegalBoard();
        board.SetPiece(0, new Piece(PieceColor.Red, PieceType.Rook));
        board.SetPiece(1, new Piece(PieceColor.Red, PieceType.Rook));
        board.SetPiece(2, new Piece(PieceColor.Red, PieceType.Rook)); // 第 3 個

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("紅俥") && e.Contains("超出上限"));
    }

    // ═══ Validate 位置規則（全盤掃描） ═══════════════════════════════════

    [Fact]
    public void Validate_AdvisorOutsidePalace_ShouldReturnPositionError()
    {
        var board = BuildMinimalLegalBoard();
        // 紅仕放在 (5,0)=45，不在九宮對角線
        board.SetPiece(45, new Piece(PieceColor.Red, PieceType.Advisor));

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("紅仕") && e.Contains("對角線"));
    }

    [Fact]
    public void Validate_ElephantCrossedRiver_ShouldReturnPositionError()
    {
        var board = BuildMinimalLegalBoard();
        // 紅相放在 (0,2)=2，這是黑方半場
        board.SetPiece(2, new Piece(PieceColor.Red, PieceType.Elephant));

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("紅相"));
    }

    [Fact]
    public void Validate_PawnOnBackRow_ShouldReturnPositionError()
    {
        var board = BuildMinimalLegalBoard();
        // 紅兵放在 row 8 = index 72
        board.SetPiece(72, new Piece(PieceColor.Red, PieceType.Pawn));

        var result = BoardValidator.Validate(board);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("紅兵") && e.Contains("底線"));
    }

    // ═══ ValidatePlacement 邊界測試 ══════════════════════════════════════

    [Fact]
    public void ValidatePlacement_NonePiece_ShouldReturnNull()
    {
        var result = BoardValidator.ValidatePlacement(0, Piece.None);
        Assert.Null(result);
    }

    [Fact]
    public void ValidatePlacement_IndexOutOfRange_ShouldReturnError()
    {
        var result = BoardValidator.ValidatePlacement(-1, new Piece(PieceColor.Red, PieceType.Rook));
        Assert.NotNull(result);

        result = BoardValidator.ValidatePlacement(90, new Piece(PieceColor.Red, PieceType.Rook));
        Assert.NotNull(result);
    }
}
