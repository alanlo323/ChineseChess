using ChineseChess.Domain.Enums;

namespace ChineseChess.Application.Models;

public sealed record GameAnalysisRequest
{
    public string Fen { get; init; } = string.Empty;
    public PieceColor MovedBy { get; init; } = PieceColor.Red;
    public string LastMoveNotation { get; init; } = string.Empty;
    public int Score { get; init; }
    public int SearchDepth { get; init; }
    public long Nodes { get; init; }
    public string? PrincipalVariation { get; init; }
    public int MoveNumber { get; init; }
}
