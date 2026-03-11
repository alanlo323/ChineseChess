using ChineseChess.Domain.Enums;

namespace ChineseChess.Application.Models;

public sealed record HintExplanationRequest
{
    public string Fen { get; init; } = string.Empty;
    public PieceColor SideToMove { get; init; } = PieceColor.Red;
    public string BestMoveNotation { get; init; } = string.Empty;
    public int Score { get; init; }
    public int SearchDepth { get; init; }
    public long Nodes { get; init; }
    public string? PrincipalVariation { get; init; }
    public string? ThinkingTree { get; init; }
}

