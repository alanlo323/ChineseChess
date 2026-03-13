using ChineseChess.Domain.Entities;
using System.Collections.Generic;
using System.Linq;

namespace ChineseChess.Application.Models;

/// <summary>開局庫中單一候選走法及其權重。</summary>
public readonly record struct OpeningBookMove(Move Move, int Weight);

/// <summary>開局庫中一個局面的所有候選走法。</summary>
public sealed class OpeningBookEntry
{
    public ulong ZobristKey { get; init; }
    public IReadOnlyList<OpeningBookMove> Moves { get; init; } = [];
    public int TotalWeight => Moves.Sum(m => m.Weight);
}
