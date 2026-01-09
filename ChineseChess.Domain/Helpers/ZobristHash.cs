using ChineseChess.Domain.Enums;
using System;

namespace ChineseChess.Domain.Helpers;

public static class ZobristHash
{
    // 90 board positions, 14 piece types (7 types * 2 colors)
    // Index mapping: (Color - 1) * 7 + (Type - 1)
    // Red: 0-6, Black: 7-13
    private static readonly ulong[,] PieceKeys = new ulong[90, 14];
    public static readonly ulong SideToMoveKey;

    static ZobristHash()
    {
        var rng = new Random(12345); // Fixed seed for reproducibility

        // Initialize piece keys
        for (int i = 0; i < 90; i++)
        {
            for (int j = 0; j < 14; j++)
            {
                PieceKeys[i, j] = NextUlong(rng);
            }
        }

        SideToMoveKey = NextUlong(rng);
    }

    private static ulong NextUlong(Random rng)
    {
        byte[] buffer = new byte[8];
        rng.NextBytes(buffer);
        return BitConverter.ToUInt64(buffer, 0);
    }

    public static ulong GetPieceKey(int position, PieceColor color, PieceType type)
    {
        if (color == PieceColor.None || type == PieceType.None) return 0;
        
        int pieceIndex = ((int)color - 1) * 7 + ((int)type - 1);
        return PieceKeys[position, pieceIndex];
    }
}
