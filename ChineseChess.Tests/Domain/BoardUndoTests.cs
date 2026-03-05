using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// MakeMove / UnmakeMove / MakeNullMove / UnmakeNullMove 正確性測試。
/// </summary>
public class BoardUndoTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── MakeMove / UnmakeMove ────────────────────────────────────

    [Fact]
    public void UnmakeMove_RestoresAllPiecePositions()
    {
        var board = new Board(InitialFen);
        var move = new Move(64, 65); // 紅砲從 (7,1) 移到 (7,2)

        // 記錄原始棋盤
        var before = new Board(InitialFen);

        board.MakeMove(move);
        board.UnmakeMove(move);

        for (int i = 0; i < Board.BoardSize; i++)
        {
            Assert.Equal(before.GetPiece(i), board.GetPiece(i));
        }
    }

    [Fact]
    public void UnmakeMove_RestoresTurn()
    {
        var board = new Board(InitialFen);
        Assert.Equal(PieceColor.Red, board.Turn);

        var move = new Move(64, 65);
        board.MakeMove(move);
        Assert.Equal(PieceColor.Black, board.Turn);

        board.UnmakeMove(move);
        Assert.Equal(PieceColor.Red, board.Turn);
    }

    [Fact]
    public void UnmakeMove_RestoresZobristKey()
    {
        var board = new Board(InitialFen);
        var originalKey = board.ZobristKey;

        var move = new Move(64, 65);
        board.MakeMove(move);
        Assert.NotEqual(originalKey, board.ZobristKey);

        board.UnmakeMove(move);
        Assert.Equal(originalKey, board.ZobristKey);
    }

    [Fact]
    public void UnmakeMove_RestoresCapturedPiece()
    {
        // 紅車在 (9,0)=81，黑兵在 (9,1)=82（特殊局面）
        // 紅車吃黑兵後回退，黑兵應回到原位
        var board = new Board("k8/9/9/9/9/9/9/9/9/Rp6K w - - 0 1");
        var captureMove = new Move(81, 82);

        var pieceAtTarget = board.GetPiece(82);
        Assert.Equal(PieceType.Pawn, pieceAtTarget.Type);
        Assert.Equal(PieceColor.Black, pieceAtTarget.Color);

        board.MakeMove(captureMove);
        Assert.Equal(PieceType.Rook, board.GetPiece(82).Type);
        Assert.True(board.GetPiece(81).IsNone);

        board.UnmakeMove(captureMove);
        Assert.Equal(PieceType.Rook, board.GetPiece(81).Type);
        Assert.Equal(PieceType.Pawn, board.GetPiece(82).Type);
        Assert.Equal(PieceColor.Black, board.GetPiece(82).Color);
    }

    [Fact]
    public void UnmakeMove_MultipleMovesAndUndos_RestoresOriginalState()
    {
        var board = new Board(InitialFen);
        var originalKey = board.ZobristKey;

        var move1 = new Move(64, 65); // 紅砲移動
        var move2 = new Move(19, 20); // 黑砲移動
        var move3 = new Move(81, 80); // 紅車移動

        board.MakeMove(move1);
        board.MakeMove(move2);
        board.MakeMove(move3);

        board.UnmakeMove(move3);
        board.UnmakeMove(move2);
        board.UnmakeMove(move1);

        Assert.Equal(originalKey, board.ZobristKey);
        Assert.Equal(PieceColor.Red, board.Turn);

        var reference = new Board(InitialFen);
        for (int i = 0; i < Board.BoardSize; i++)
        {
            Assert.Equal(reference.GetPiece(i), board.GetPiece(i));
        }
    }

    [Fact]
    public void UnmakeMove_EmptyHistory_ThrowsInvalidOperation()
    {
        var board = new Board(InitialFen);
        Assert.Throws<InvalidOperationException>(() => board.UnmakeMove(new Move(64, 65)));
    }

    // ─── TryGetLastMove ───────────────────────────────────────────

    [Fact]
    public void TryGetLastMove_Initially_ReturnsFalse()
    {
        var board = new Board(InitialFen);
        Assert.False(board.TryGetLastMove(out _));
    }

    [Fact]
    public void TryGetLastMove_AfterOneMove_ReturnsMove()
    {
        var board = new Board(InitialFen);
        var move = new Move(64, 65);
        board.MakeMove(move);

        Assert.True(board.TryGetLastMove(out var lastMove));
        Assert.Equal(move, lastMove);
    }

    [Fact]
    public void TryGetLastMove_AfterMultipleMoves_ReturnsMostRecent()
    {
        var board = new Board(InitialFen);
        var move1 = new Move(64, 65);
        var move2 = new Move(19, 20);
        board.MakeMove(move1);
        board.MakeMove(move2);

        Assert.True(board.TryGetLastMove(out var lastMove));
        Assert.Equal(move2, lastMove);
    }

    [Fact]
    public void TryGetLastMove_AfterUnmake_ReturnsCorrectPreviousMove()
    {
        var board = new Board(InitialFen);
        var move1 = new Move(64, 65);
        var move2 = new Move(19, 20);
        board.MakeMove(move1);
        board.MakeMove(move2);
        board.UnmakeMove(move2);

        Assert.True(board.TryGetLastMove(out var lastMove));
        Assert.Equal(move1, lastMove);
    }

    // ─── MakeNullMove / UnmakeNullMove ────────────────────────────

    [Fact]
    public void MakeNullMove_SwitchesTurn()
    {
        var board = new Board(InitialFen);
        Assert.Equal(PieceColor.Red, board.Turn);
        board.MakeNullMove();
        Assert.Equal(PieceColor.Black, board.Turn);
    }

    [Fact]
    public void UnmakeNullMove_RestoresTurn()
    {
        var board = new Board(InitialFen);
        board.MakeNullMove();
        board.UnmakeNullMove();
        Assert.Equal(PieceColor.Red, board.Turn);
    }

    [Fact]
    public void MakeNullMove_UpdatesZobristKey()
    {
        var board = new Board(InitialFen);
        var keyBefore = board.ZobristKey;
        board.MakeNullMove();
        Assert.NotEqual(keyBefore, board.ZobristKey);
    }

    [Fact]
    public void UnmakeNullMove_RestoresZobristKey()
    {
        var board = new Board(InitialFen);
        var originalKey = board.ZobristKey;
        board.MakeNullMove();
        board.UnmakeNullMove();
        Assert.Equal(originalKey, board.ZobristKey);
    }

    [Fact]
    public void MakeNullMove_DoesNotChangePiecePositions()
    {
        var board = new Board(InitialFen);
        var reference = new Board(InitialFen);
        board.MakeNullMove();

        for (int i = 0; i < Board.BoardSize; i++)
        {
            Assert.Equal(reference.GetPiece(i), board.GetPiece(i));
        }
    }

    [Fact]
    public void UnmakeNullMove_EmptyHistory_ThrowsInvalidOperation()
    {
        var board = new Board(InitialFen);
        Assert.Throws<InvalidOperationException>(() => board.UnmakeNullMove());
    }

    // ─── UnmakeMove via UndoMove ──────────────────────────────────

    [Fact]
    public void UndoMove_RestoresStateCorrectly()
    {
        var board = new Board(InitialFen);
        var originalKey = board.ZobristKey;
        var move = new Move(64, 65);

        board.MakeMove(move);
        board.UndoMove();

        Assert.Equal(originalKey, board.ZobristKey);
        Assert.Equal(PieceColor.Red, board.Turn);
    }

    // ─── Clone 不共享歷史堆疊 ─────────────────────────────────────

    [Fact]
    public void Clone_DoesNotShareHistory()
    {
        var board = new Board(InitialFen);
        var move = new Move(64, 65);
        board.MakeMove(move);

        var clone = (Board)board.Clone();

        // Clone 不應持有原始棋盤的歷史
        Assert.False(clone.TryGetLastMove(out _));
    }

    [Fact]
    public void Clone_CopiesPiecePositions()
    {
        var board = new Board(InitialFen);
        var clone = (Board)board.Clone();

        for (int i = 0; i < Board.BoardSize; i++)
        {
            Assert.Equal(board.GetPiece(i), clone.GetPiece(i));
        }
        Assert.Equal(board.ZobristKey, clone.ZobristKey);
        Assert.Equal(board.Turn, clone.Turn);
    }
}
