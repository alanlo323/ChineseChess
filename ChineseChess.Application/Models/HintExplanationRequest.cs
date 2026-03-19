using ChineseChess.Domain.Enums;
using System.Collections.Generic;

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

    /// <summary>MultiPV 其餘候選走法（供比較式解釋用）。</summary>
    public IReadOnlyList<AlternativeMoveInfo>? AlternativeMoves { get; init; }
}

/// <summary>MultiPV 候選走法資訊（rank#2 以後）。</summary>
public sealed record AlternativeMoveInfo(int Rank, string Notation, int Score, string PvLine);

