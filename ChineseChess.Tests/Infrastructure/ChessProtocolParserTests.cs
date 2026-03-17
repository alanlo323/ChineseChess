using ChineseChess.Infrastructure.AI.Protocol;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// ChessProtocolParser 命令解析測試。
/// 涵蓋 UCI / UCCI 所有標準命令及邊界情況。
/// </summary>
public class ChessProtocolParserTests
{
    // ─── 握手命令 ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Uci_ShouldReturnUciCommand()
    {
        var args = ChessProtocolParser.Parse("uci");
        Assert.Equal(ChessCommand.Uci, args.Command);
    }

    [Fact]
    public void Parse_Ucci_ShouldReturnUcciCommand()
    {
        var args = ChessProtocolParser.Parse("ucci");
        Assert.Equal(ChessCommand.Ucci, args.Command);
    }

    [Fact]
    public void Parse_IsReady_ShouldReturnIsReadyCommand()
    {
        var args = ChessProtocolParser.Parse("isready");
        Assert.Equal(ChessCommand.IsReady, args.Command);
    }

    [Fact]
    public void Parse_Stop_ShouldReturnStopCommand()
    {
        var args = ChessProtocolParser.Parse("stop");
        Assert.Equal(ChessCommand.Stop, args.Command);
    }

    [Fact]
    public void Parse_Quit_ShouldReturnQuitCommand()
    {
        var args = ChessProtocolParser.Parse("quit");
        Assert.Equal(ChessCommand.Quit, args.Command);
    }

    // ─── position 命令 ────────────────────────────────────────────────────

    [Fact]
    public void Parse_PositionFen_ShouldExtractFen()
    {
        const string fen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";
        var args = ChessProtocolParser.Parse($"position fen {fen}");

        Assert.Equal(ChessCommand.Position, args.Command);
        Assert.Equal(fen, args.Fen);
        Assert.Empty(args.Moves);
    }

    [Fact]
    public void Parse_PositionFenWithMoves_ShouldExtractFenAndMoves()
    {
        const string fen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";
        var args = ChessProtocolParser.Parse($"position fen {fen} moves h2e2 h9g7");

        Assert.Equal(ChessCommand.Position, args.Command);
        Assert.Equal(fen, args.Fen);
        Assert.Equal(2, args.Moves.Count);
        Assert.Equal("h2e2", args.Moves[0]);
        Assert.Equal("h9g7", args.Moves[1]);
    }

    [Fact]
    public void Parse_PositionWithEmptyMovesList_ShouldReturnEmptyMoves()
    {
        const string fen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";
        var args = ChessProtocolParser.Parse($"position fen {fen} moves");

        Assert.Equal(ChessCommand.Position, args.Command);
        // "moves" 關鍵字後無走法
        Assert.Empty(args.Moves);
    }

    // ─── go 命令 ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_GoMovetime_ShouldExtractMoveTime()
    {
        var args = ChessProtocolParser.Parse("go movetime 1000");

        Assert.Equal(ChessCommand.Go, args.Command);
        Assert.Equal(1000, args.MoveTime);
        Assert.Null(args.Depth);
        Assert.False(args.IsInfinite);
    }

    [Fact]
    public void Parse_GoDepth_ShouldExtractDepth()
    {
        var args = ChessProtocolParser.Parse("go depth 5");

        Assert.Equal(ChessCommand.Go, args.Command);
        Assert.Equal(5, args.Depth);
        Assert.Null(args.MoveTime);
        Assert.False(args.IsInfinite);
    }

    [Fact]
    public void Parse_GoInfinite_ShouldSetIsInfinite()
    {
        var args = ChessProtocolParser.Parse("go infinite");

        Assert.Equal(ChessCommand.Go, args.Command);
        Assert.True(args.IsInfinite);
        Assert.Null(args.MoveTime);
        Assert.Null(args.Depth);
    }

    // ─── 未知命令與空行 ───────────────────────────────────────────────────

    [Fact]
    public void Parse_UnknownCommand_ShouldReturnUnknown()
    {
        var args = ChessProtocolParser.Parse("xyzcommand");
        Assert.Equal(ChessCommand.Unknown, args.Command);
    }

    [Fact]
    public void Parse_EmptyLine_ShouldReturnUnknown()
    {
        var args = ChessProtocolParser.Parse("");
        Assert.Equal(ChessCommand.Unknown, args.Command);
    }

    [Fact]
    public void Parse_RawPreserved_ShouldMatchInput()
    {
        const string input = "go movetime 2000";
        var args = ChessProtocolParser.Parse(input);
        Assert.Equal(input, args.Raw);
    }
}
