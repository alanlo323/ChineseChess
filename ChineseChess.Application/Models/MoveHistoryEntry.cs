using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Application.Models;

/// <summary>GameService 對外暴露的走法歷史條目（含記譜）。</summary>
public sealed record MoveHistoryEntry
{
    /// <summary>步號（1-based）。</summary>
    public required int StepNumber { get; init; }

    /// <summary>走法資料。</summary>
    public required Move Move { get; init; }

    /// <summary>中文記譜，如「馬8進7」。</summary>
    public required string Notation { get; init; }

    /// <summary>走棋方顏色。</summary>
    public required PieceColor Turn { get; init; }

    /// <summary>是否為吃子著法。</summary>
    public bool IsCapture { get; init; }
}
