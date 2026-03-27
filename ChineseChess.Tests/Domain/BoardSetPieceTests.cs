using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// 測試 Board.SetPiece 與 Board.SetTurn 的功能正確性，
/// 包含棋子放置、Zobrist 雜湊更新與將/帥位置快取同步。
/// </summary>
public class BoardSetPieceTests
{
    // ─── SetPiece：基本放置 ─────────────────────────────────────────────

    [Fact]
    public void SetPiece_OnEmptySquare_ShouldPlacePieceCorrectly()
    {
        var board = new Board();
        var piece = new Piece(PieceColor.Red, PieceType.Rook);

        board.SetPiece(0, piece);

        Assert.Equal(piece, board.GetPiece(0));
    }

    [Fact]
    public void SetPiece_WithNone_ShouldRemovePiece()
    {
        var board = new Board();
        board.ParseFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");

        board.SetPiece(0, Piece.None); // 移除左上角黑方車

        Assert.Equal(Piece.None, board.GetPiece(0));
    }

    [Fact]
    public void SetPiece_ReplaceExistingPiece_ShouldUpdateCorrectly()
    {
        var board = new Board();
        board.ParseFen("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");

        var redCannon = new Piece(PieceColor.Red, PieceType.Cannon);
        board.SetPiece(0, redCannon); // 取代原本的黑方車

        Assert.Equal(redCannon, board.GetPiece(0));
    }

    // ─── SetPiece：Zobrist 雜湊正確性 ──────────────────────────────────

    [Fact]
    public void SetPiece_ShouldUpdateZobristKey()
    {
        var board = new Board();
        ulong keyBefore = board.ZobristKey;

        board.SetPiece(10, new Piece(PieceColor.Red, PieceType.Rook));

        Assert.NotEqual(keyBefore, board.ZobristKey);
    }

    [Fact]
    public void SetPiece_TwoIdenticalOperations_ShouldProduceConsistentKey()
    {
        var board1 = new Board();
        var board2 = new Board();
        var piece = new Piece(PieceColor.Black, PieceType.Cannon);

        board1.SetPiece(20, piece);
        board2.SetPiece(20, piece);

        Assert.Equal(board1.ZobristKey, board2.ZobristKey);
    }

    [Fact]
    public void SetPiece_PlaceThenRemoveSamePiece_ShouldRestoreOriginalKey()
    {
        var board = new Board();
        ulong keyBefore = board.ZobristKey;
        var piece = new Piece(PieceColor.Red, PieceType.Horse);

        board.SetPiece(5, piece);
        board.SetPiece(5, Piece.None); // 移除

        Assert.Equal(keyBefore, board.ZobristKey);
    }

    [Fact]
    public void SetPiece_ReplaceWithDifferentPiece_ShouldUpdateZobristCorrectly()
    {
        var board = new Board();
        var oldPiece = new Piece(PieceColor.Red, PieceType.Cannon);
        var newPiece = new Piece(PieceColor.Black, PieceType.Rook);

        board.SetPiece(15, oldPiece);
        ulong keyAfterPlace = board.ZobristKey;

        board.SetPiece(15, newPiece);
        ulong keyAfterReplace = board.ZobristKey;

        // 兩個不同棋子產生不同 Key
        Assert.NotEqual(keyAfterPlace, keyAfterReplace);

        // 計算預期：先 XOR 掉舊棋子，再 XOR 新棋子
        ulong expectedKey = keyAfterPlace
            ^ ZobristHash.GetPieceKey(15, oldPiece.Color, oldPiece.Type)
            ^ ZobristHash.GetPieceKey(15, newPiece.Color, newPiece.Type);
        Assert.Equal(expectedKey, keyAfterReplace);
    }

    // ─── SetPiece：將/帥位置快取 ────────────────────────────────────────

    [Fact]
    public void SetPiece_RedKing_ShouldUpdateKingCacheAndEnableIsCheck()
    {
        var board = new Board();
        // 放紅帥到九宮中心（row=9, col=4 → index=85）
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));
        // 放黑將（row=0, col=4 → index=4）
        board.SetPiece(4, new Piece(PieceColor.Black, PieceType.King));

        // 紅帥在場，IsCheck 應能正確執行不拋出例外
        var exception = Record.Exception(() => board.IsCheck(PieceColor.Red));
        Assert.Null(exception);
    }

    [Fact]
    public void SetPiece_MoveKingToNewPosition_ShouldUpdateCache()
    {
        var board = new Board();
        // index 85 = row9,col4（紅帥標準初始位）
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));
        board.SetPiece(4, new Piece(PieceColor.Black, PieceType.King));
        board.SetPiece(76, new Piece(PieceColor.Red, PieceType.King)); // 移動到 row8,col4
        board.SetPiece(85, Piece.None);

        // 驗證棋盤狀態一致（King 快取應已更新）
        Assert.Equal(Piece.None, board.GetPiece(85));
        Assert.Equal(new Piece(PieceColor.Red, PieceType.King), board.GetPiece(76));
    }

    // ─── SetTurn ────────────────────────────────────────────────────────

    [Fact]
    public void SetTurn_ToBlack_ShouldUpdateTurnAndZobrist()
    {
        var board = new Board();
        // 預設 turn = Red
        ulong keyBefore = board.ZobristKey;

        board.SetTurn(PieceColor.Black);

        Assert.Equal(PieceColor.Black, board.Turn);
        // 換手會 XOR SideToMoveKey
        Assert.Equal(keyBefore ^ ZobristHash.SideToMoveKey, board.ZobristKey);
    }

    [Fact]
    public void SetTurn_ToRed_WhenAlreadyRed_ShouldNotChangeKey()
    {
        var board = new Board();
        ulong keyBefore = board.ZobristKey;

        board.SetTurn(PieceColor.Red); // 已是 Red，應不改變

        Assert.Equal(PieceColor.Red, board.Turn);
        Assert.Equal(keyBefore, board.ZobristKey);
    }

    [Fact]
    public void SetTurn_ToggleTwice_ShouldRestoreOriginalKey()
    {
        var board = new Board();
        ulong keyBefore = board.ZobristKey;

        board.SetTurn(PieceColor.Black);
        board.SetTurn(PieceColor.Red);

        Assert.Equal(PieceColor.Red, board.Turn);
        Assert.Equal(keyBefore, board.ZobristKey);
    }

    // ─── SetPiece：邊界條件 ─────────────────────────────────────────────

    [Fact]
    public void SetPiece_IndexZero_ShouldWork()
    {
        var board = new Board();
        var piece = new Piece(PieceColor.Black, PieceType.Advisor);
        board.SetPiece(0, piece);
        Assert.Equal(piece, board.GetPiece(0));
    }

    [Fact]
    public void SetPiece_IndexMaximum_ShouldWork()
    {
        var board = new Board();
        var piece = new Piece(PieceColor.Red, PieceType.Advisor);
        board.SetPiece(89, piece);
        Assert.Equal(piece, board.GetPiece(89));
    }

    [Fact]
    public void SetPiece_InvalidIndex_ShouldThrow()
    {
        var board = new Board();
        Assert.Throws<ArgumentOutOfRangeException>(() => board.SetPiece(-1, Piece.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => board.SetPiece(90, Piece.None));
    }

    [Fact]
    public void SetPiece_None_OnEmptySquare_ShouldKeepSameKey()
    {
        var board = new Board();
        ulong keyBefore = board.ZobristKey;

        board.SetPiece(50, Piece.None); // 空格設空，不應改變 Key

        Assert.Equal(keyBefore, board.ZobristKey);
    }
}
