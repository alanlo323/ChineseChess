using System;
using System.Collections.Generic;

namespace ChineseChess.Infrastructure.AI.Protocol;

/// <summary>UCI / UCCI 命令類型。</summary>
public enum ChessCommand
{
    Uci,
    Ucci,
    IsReady,
    Position,
    Go,
    Stop,
    Quit,
    Unknown
}

/// <summary>解析後的 UCI / UCCI 命令參數。</summary>
public sealed class ChessCommandArgs
{
    public ChessCommand Command { get; init; }

    /// <summary>position 命令中的 FEN 字串（僅 Command == Position 時有值）。</summary>
    public string? Fen { get; init; }

    /// <summary>position 命令中的走法序列（可為空）。</summary>
    public IReadOnlyList<string> Moves { get; init; } = [];

    /// <summary>go movetime 的毫秒數（僅 Command == Go 且含 movetime 時有值）。</summary>
    public int? MoveTime { get; init; }

    /// <summary>go depth 的深度（僅 Command == Go 且含 depth 時有值）。</summary>
    public int? Depth { get; init; }

    /// <summary>go infinite 旗標。</summary>
    public bool IsInfinite { get; init; }

    /// <summary>原始輸入字串。</summary>
    public string Raw { get; init; } = string.Empty;
}

/// <summary>
/// UCI / UCCI 命令行解析器（純函式，無副作用）。
/// 支援命令：uci、ucci、isready、position、go、stop、quit。
/// </summary>
public static class ChessProtocolParser
{
    /// <summary>解析一行 UCI/UCCI 命令字串，回傳對應的 <see cref="ChessCommandArgs"/>。</summary>
    public static ChessCommandArgs Parse(string line)
    {
        var raw = line?.Trim() ?? string.Empty;
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return new ChessCommandArgs { Command = ChessCommand.Unknown, Raw = raw };

        return parts[0] switch
        {
            "uci"     => new ChessCommandArgs { Command = ChessCommand.Uci,     Raw = raw },
            "ucci"    => new ChessCommandArgs { Command = ChessCommand.Ucci,    Raw = raw },
            "isready" => new ChessCommandArgs { Command = ChessCommand.IsReady, Raw = raw },
            "stop"    => new ChessCommandArgs { Command = ChessCommand.Stop,    Raw = raw },
            "quit"    => new ChessCommandArgs { Command = ChessCommand.Quit,    Raw = raw },
            "position" => ParsePosition(parts, raw),
            "go"       => ParseGo(parts, raw),
            _          => new ChessCommandArgs { Command = ChessCommand.Unknown, Raw = raw }
        };
    }

    // ─── 私有解析輔助 ─────────────────────────────────────────────────────

    private static ChessCommandArgs ParsePosition(string[] parts, string raw)
    {
        // 格式：position fen <FEN> [moves m1 m2 ...]
        string? fen = null;
        var moves = new List<string>();

        if (parts.Length >= 2 && parts[1] == "fen")
        {
            int movesIdx = Array.IndexOf(parts, "moves");
            if (movesIdx > 0)
            {
                fen = string.Join(" ", parts[2..movesIdx]);
                moves.AddRange(parts[(movesIdx + 1)..]);
            }
            else
            {
                fen = string.Join(" ", parts[2..]);
            }
        }

        return new ChessCommandArgs
        {
            Command = ChessCommand.Position,
            Fen = fen,
            Moves = moves,
            Raw = raw
        };
    }

    private static ChessCommandArgs ParseGo(string[] parts, string raw)
    {
        // 格式：go [movetime <ms>] [depth <d>] [infinite]
        int? moveTime = null;
        int? depth = null;
        bool isInfinite = false;

        for (int i = 1; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case "movetime" when i + 1 < parts.Length && int.TryParse(parts[i + 1], out int mt):
                    moveTime = mt;
                    break;
                case "depth" when i + 1 < parts.Length && int.TryParse(parts[i + 1], out int d):
                    depth = d;
                    break;
                case "infinite":
                    isInfinite = true;
                    break;
            }
        }

        return new ChessCommandArgs
        {
            Command = ChessCommand.Go,
            MoveTime = moveTime,
            Depth = depth,
            IsInfinite = isInfinite,
            Raw = raw
        };
    }
}
