using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// FEN 字串解析與生成的完整性測試。
/// 棋盤索引：index = row * 9 + col（row 0 = 上方黑方底線，row 9 = 下方紅方底線）。
/// </summary>
public class FenTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    [Fact]
    public void ParseFen_InitialPosition_AllPiecesCorrect()
    {
        var board = new Board(InitialFen);

        // 黑方底線（row 0）
        Assert.Equal(new Piece(PieceColor.Black, PieceType.Rook),     board.GetPiece(0));  // (0,0)
        Assert.Equal(new Piece(PieceColor.Black, PieceType.Horse),    board.GetPiece(1));  // (0,1)
        Assert.Equal(new Piece(PieceColor.Black, PieceType.Elephant), board.GetPiece(2));  // (0,2)
        Assert.Equal(new Piece(PieceColor.Black, PieceType.Advisor),  board.GetPiece(3));  // (0,3)
        Assert.Equal(new Piece(PieceColor.Black, PieceType.King),     board.GetPiece(4));  // (0,4)
        Assert.Equal(new Piece(PieceColor.Black, PieceType.Advisor),  board.GetPiece(5));  // (0,5)
        Assert.Equal(new Piece(PieceColor.Black, PieceType.Elephant), board.GetPiece(6));  // (0,6)
        Assert.Equal(new Piece(PieceColor.Black, PieceType.Horse),    board.GetPiece(7));  // (0,7)
        Assert.Equal(new Piece(PieceColor.Black, PieceType.Rook),     board.GetPiece(8));  // (0,8)

        // 紅方底線（row 9）
        Assert.Equal(new Piece(PieceColor.Red, PieceType.Rook),     board.GetPiece(81)); // (9,0)
        Assert.Equal(new Piece(PieceColor.Red, PieceType.Horse),    board.GetPiece(82)); // (9,1)
        Assert.Equal(new Piece(PieceColor.Red, PieceType.Elephant), board.GetPiece(83)); // (9,2)
        Assert.Equal(new Piece(PieceColor.Red, PieceType.Advisor),  board.GetPiece(84)); // (9,3)
        Assert.Equal(new Piece(PieceColor.Red, PieceType.King),     board.GetPiece(85)); // (9,4)
        Assert.Equal(new Piece(PieceColor.Red, PieceType.Advisor),  board.GetPiece(86)); // (9,5)
        Assert.Equal(new Piece(PieceColor.Red, PieceType.Elephant), board.GetPiece(87)); // (9,6)
        Assert.Equal(new Piece(PieceColor.Red, PieceType.Horse),    board.GetPiece(88)); // (9,7)
        Assert.Equal(new Piece(PieceColor.Red, PieceType.Rook),     board.GetPiece(89)); // (9,8)

        // 行棋方
        Assert.Equal(PieceColor.Red, board.Turn);
    }

    [Fact]
    public void ParseFen_BlackTurn_SetsTurnCorrectly()
    {
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR b - - 0 1");
        Assert.Equal(PieceColor.Black, board.Turn);
    }

    [Fact]
    public void ParseFen_StandardElephantAndHorseChars_Parsed()
    {
        // 標準 FEN 使用 'b'/'B' 表示象，'n'/'N' 表示馬
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        Assert.Equal(PieceType.Horse,    board.GetPiece(1).Type);  // 'n'
        Assert.Equal(PieceType.Elephant, board.GetPiece(2).Type);  // 'b'
        Assert.Equal(PieceType.Horse,    board.GetPiece(88).Type); // 'N'
        Assert.Equal(PieceType.Elephant, board.GetPiece(87).Type); // 'B'
    }

    [Fact]
    public void ToFen_ThenParseFen_PreservesZobristKey()
    {
        // ParseFen → ToFen → ParseFen 應產生相同的 Zobrist Key
        var board1 = new Board(InitialFen);
        var fen = board1.ToFen();
        var board2 = new Board(fen);

        Assert.Equal(board1.ZobristKey, board2.ZobristKey);
    }

    [Fact]
    public void ToFen_ThenParseFen_PreservesAllPiecePositions()
    {
        var board1 = new Board(InitialFen);
        var board2 = new Board(board1.ToFen());

        for (int i = 0; i < 90; i++)
        {
            Assert.Equal(board1.GetPiece(i), board2.GetPiece(i));
        }
        Assert.Equal(board1.Turn, board2.Turn);
    }

    [Fact]
    public void ToFen_UsesStandardFenChars_ForElephantAndHorse()
    {
        // 生成的 FEN 應使用標準字符（B/b 象，N/n 馬）
        var board = new Board(InitialFen);
        var fen = board.ToFen();

        // 第一行包含黑方棋子的 FEN 標示
        Assert.Contains("n", fen); // 黑馬
        Assert.Contains("b", fen); // 黑象
        Assert.Contains("N", fen); // 紅馬
        Assert.Contains("B", fen); // 紅象
    }

    /// <summary>
    /// 回歸測試：確認開局車馬位置正確。
    /// 背景：PieceTextConverter 曾將 Horse 和 Rook 的中文字對調，
    /// 導致畫面上車和馬看起來位置互換，但 Domain 層的類型本身一直是正確的。
    /// </summary>
    [Fact]
    public void InitialPosition_CornersAreRooks_AndAdjacentAreHorses()
    {
        var board = new Board(InitialFen);

        // 四個角落必須是車（Rook），而非馬（Horse）
        Assert.Equal(PieceType.Rook, board.GetPiece(0).Type);   // 黑方左車 (row0, col0)
        Assert.Equal(PieceType.Rook, board.GetPiece(8).Type);   // 黑方右車 (row0, col8)
        Assert.Equal(PieceType.Rook, board.GetPiece(81).Type);  // 紅方左車 (row9, col0)
        Assert.Equal(PieceType.Rook, board.GetPiece(89).Type);  // 紅方右車 (row9, col8)

        // 角落旁邊必須是馬（Horse），而非車（Rook）
        Assert.Equal(PieceType.Horse, board.GetPiece(1).Type);  // 黑方左馬 (row0, col1)
        Assert.Equal(PieceType.Horse, board.GetPiece(7).Type);  // 黑方右馬 (row0, col7)
        Assert.Equal(PieceType.Horse, board.GetPiece(82).Type); // 紅方左馬 (row9, col1)
        Assert.Equal(PieceType.Horse, board.GetPiece(88).Type); // 紅方右馬 (row9, col7)
    }

    [Fact]
    public void ParseFen_EmptyBoard_AllPiecesNone()
    {
        var board = new Board("9/9/9/9/9/9/9/9/9/9 w - - 0 1");
        for (int i = 0; i < 90; i++)
        {
            Assert.True(board.GetPiece(i).IsNone);
        }
    }

    [Fact]
    public void ParseFen_Repeated_ResetsBoard()
    {
        // 解析不同 FEN 後，棋盤應完全重置
        var board = new Board(InitialFen);
        board.ParseFen("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");

        // 原本有棋子的位置現在應為空
        Assert.True(board.GetPiece(0).IsNone);   // 黑車原始位置
        Assert.True(board.GetPiece(89).IsNone);  // 紅車原始位置

        // 新設定的棋子
        Assert.Equal(PieceType.King, board.GetPiece(4).Type);  // 黑將 (0,4)
        Assert.Equal(PieceType.King, board.GetPiece(85).Type); // 紅帥 (9,4)
    }

    [Fact]
    public void ParseFen_InvalidRowCount_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9 w - - 0 1");
        });
    }

    [Fact]
    public void ParseFen_InvalidRowLength_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/4 w - - 0 1");
        });

        Assert.Throws<ArgumentException>(() =>
        {
            new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/123/9/9/P1P1P1P1P/1C5C1/9 w - - 0 1");
        });
    }

    [Fact]
    public void ParseFen_UnknownPiece_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNz w - - 0 1");
        });
    }

    [Fact]
    public void ParseFen_InvalidSideToMove_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR x - - 0 1");
        });
    }

    [Fact]
    public void ParseFen_EmptyString_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            new Board(string.Empty);
        });
    }

    [Fact]
    public void GetPiece_OutOfRange_ReturnsNone()
    {
        var board = new Board(InitialFen);

        Assert.True(board.GetPiece(-1).IsNone);
        Assert.True(board.GetPiece(90).IsNone);
    }
}
