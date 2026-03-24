using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.AI.Nnue.Helpers;

/// <summary>
/// LayerStack 選桶邏輯（推論與訓練共用）。
/// 桶號依雙方現存的車數（0-2）及馬/炮合計數（0-4）決定，共 16 桶。
/// </summary>
internal static class LayerStackBucketHelper
{
    private static readonly byte[,,,] LayerStackBuckets = BuildLayerStackBuckets();

    private static byte[,,,] BuildLayerStackBuckets()
    {
        var v = new byte[3, 3, 5, 5];
        for (int ur = 0; ur <= 2; ur++)
            for (int or_ = 0; or_ <= 2; or_++)
                for (int ukc = 0; ukc <= 4; ukc++)
                    for (int okc = 0; okc <= 4; okc++)
                    {
                        int b;
                        if (ur == or_)
                            b = ur * 4 + (ukc + okc >= 4 ? 2 : 0) + (ukc == okc ? 1 : 0);
                        else if (ur == 2 && or_ == 1) b = 12;
                        else if (ur == 1 && or_ == 2) b = 13;
                        else if (ur >  0 && or_ == 0) b = 14;
                        else b = 15;  // ur==0 && or_>0
                        v[ur, or_, ukc, okc] = (byte)b;
                    }
        return v;
    }

    /// <summary>依當前局面的大子數計算 LayerStack 桶號（0-15）。</summary>
    public static int GetBucket(IBoard board)
    {
        var us   = board.Turn;
        var them = us == PieceColor.Red ? PieceColor.Black : PieceColor.Red;

        int usRooks = 0, usKC = 0, oppRooks = 0, oppKC = 0;
        for (int i = 0; i < 90; i++)
        {
            var p = board.GetPiece(i);
            if (p.IsNone) continue;
            if (p.Color == us)
            {
                if (p.Type == PieceType.Rook) usRooks++;
                else if (p.Type is PieceType.Horse or PieceType.Cannon) usKC++;
            }
            else if (p.Color == them)
            {
                if (p.Type == PieceType.Rook) oppRooks++;
                else if (p.Type is PieceType.Horse or PieceType.Cannon) oppKC++;
            }
        }

        return LayerStackBuckets[
            Math.Min(usRooks,  2),
            Math.Min(oppRooks, 2),
            Math.Min(usKC,     4),
            Math.Min(oppKC,    4)];
    }
}
