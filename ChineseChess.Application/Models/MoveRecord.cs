using ChineseChess.Application.Enums;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Application.Models;

/// <summary>
/// 記錄一步著法的 WXF 相關資訊，供重複局面裁決使用。
/// ZobristKey 為走完後的局面雜湊（含行棋方資訊）。
/// Turn 為發動這步著法的棋子顏色（非走完後輪到的顏色）。
/// </summary>
public sealed record MoveRecord
{
    public required ulong ZobristKey { get; init; }
    public required PieceColor Turn { get; init; }
    public required Move Move { get; init; }
    public required MoveClassification Classification { get; init; }

    /// <summary>Chase 時被追棋子的格子索引；非 Chase 時為 -1。</summary>
    public int VictimSquare { get; init; } = -1;

    public bool IsCapture { get; init; }
}
