using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.AI.Nnue.Helpers;

/// <summary>NNUE 特徵計算的共用工具方法。</summary>
internal static class NnueFeatureHelper
{
    /// <summary>在盤面上找到指定顏色的王，返回格子索引（0-89）；找不到返回 -1。</summary>
    public static int FindKing(IBoard board, PieceColor color)
    {
        for (int i = 0; i < 90; i++)
        {
            var p = board.GetPiece(i);
            if (!p.IsNone && p.Color == color && p.Type == PieceType.King) return i;
        }
        return -1;
    }
}
