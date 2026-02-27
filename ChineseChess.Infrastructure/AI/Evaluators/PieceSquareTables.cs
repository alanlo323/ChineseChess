using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.AI.Evaluators;

/// <summary>
/// 90-square Piece-Square Tables for Chinese Chess (Xiangqi).
/// Defined from Red's perspective (row 0 = Black's back rank, row 9 = Red's back rank).
/// Black's values are obtained by mirroring the index (89 - index).
/// </summary>
public static class PieceSquareTables
{
    // Board: 10 rows x 9 cols, index = row * 9 + col
    // Row 0-4 = Black's territory, Row 5-9 = Red's territory

    private static readonly int[] KingTable =
    {
        //  col: 0   1   2   3   4   5   6   7   8
        /*r0*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r1*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r2*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r3*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r4*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r5*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r6*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r7*/  0,  0,  0,  1, -8,  1,  0,  0,  0,
        /*r8*/  0,  0,  0,  2,  4,  2,  0,  0,  0,
        /*r9*/  0,  0,  0,  1,  8,  1,  0,  0,  0,
    };

    private static readonly int[] AdvisorTable =
    {
        /*r0*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r1*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r2*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r3*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r4*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r5*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r6*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r7*/  0,  0,  0, 10,  0, 10,  0,  0,  0,
        /*r8*/  0,  0,  0,  0, 15,  0,  0,  0,  0,
        /*r9*/  0,  0,  0, 10,  0, 10,  0,  0,  0,
    };

    private static readonly int[] ElephantTable =
    {
        /*r0*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r1*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r2*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r3*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r4*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r5*/  0,  0, -2,  0,  0,  0, -2,  0,  0,
        /*r6*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r7*/  6,  0,  0,  0, 15,  0,  0,  0,  6,
        /*r8*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r9*/  0,  0, 12,  0,  0,  0, 12,  0,  0,
    };

    private static readonly int[] HorseTable =
    {
        /*r0*/  0,  2,  4,  4, -2,  4,  4,  2,  0,
        /*r1*/  2,  8, 12, 12, 10, 12, 12,  8,  2,
        /*r2*/  4, 10, 16, 18, 18, 18, 16, 10,  4,
        /*r3*/  4, 12, 16, 20, 20, 20, 16, 12,  4,
        /*r4*/  6, 14, 18, 22, 22, 22, 18, 14,  6,
        /*r5*/  4, 12, 18, 22, 22, 22, 18, 12,  4,
        /*r6*/  2,  8, 14, 16, 16, 16, 14,  8,  2,
        /*r7*/  0,  4,  8, 10, 10, 10,  8,  4,  0,
        /*r8*/  0,  2,  4,  6,  6,  6,  4,  2,  0,
        /*r9*/  0,  0,  2,  4,  2,  4,  2,  0,  0,
    };

    private static readonly int[] RookTable =
    {
        /*r0*/  6, 12, 12, 18, 14, 18, 12, 12,  6,
        /*r1*/  8, 16, 16, 20, 22, 20, 16, 16,  8,
        /*r2*/  6, 14, 16, 20, 20, 20, 16, 14,  6,
        /*r3*/  8, 16, 18, 22, 22, 22, 18, 16,  8,
        /*r4*/ 12, 18, 20, 24, 24, 24, 20, 18, 12,
        /*r5*/ 12, 18, 20, 24, 26, 24, 20, 18, 12,
        /*r6*/  8, 14, 16, 20, 20, 20, 16, 14,  8,
        /*r7*/  4, 10, 12, 16, 18, 16, 12, 10,  4,
        /*r8*/  6, 10, 10, 14, 16, 14, 10, 10,  6,
        /*r9*/ -2,  6,  8, 14, 14, 14,  8,  6, -2,
    };

    private static readonly int[] CannonTable =
    {
        /*r0*/  4,  6,  8, 10, 12, 10,  8,  6,  4,
        /*r1*/  4,  8, 10, 12, 14, 12, 10,  8,  4,
        /*r2*/  4,  8, 12, 16, 16, 16, 12,  8,  4,
        /*r3*/  2,  6,  8, 12, 14, 12,  8,  6,  2,
        /*r4*/  0,  4,  6,  8, 12,  8,  6,  4,  0,
        /*r5*/  0,  2,  4,  8, 10,  8,  4,  2,  0,
        /*r6*/ -2,  0,  4,  6,  8,  6,  4,  0, -2,
        /*r7*/  0,  0,  2,  6,  6,  6,  2,  0,  0,
        /*r8*/  0,  0,  0,  4,  4,  4,  0,  0,  0,
        /*r9*/  0,  0,  0,  2,  4,  2,  0,  0,  0,
    };

    private static readonly int[] PawnTable =
    {
        /*r0*/  0,  0,  0,  6,  8,  6,  0,  0,  0,
        /*r1*/  0,  0,  0, 10, 16, 10,  0,  0,  0,
        /*r2*/  2,  2,  6, 14, 20, 14,  6,  2,  2,
        /*r3*/  6,  8, 12, 18, 26, 18, 12,  8,  6,
        /*r4*/ 10, 18, 22, 32, 40, 32, 22, 18, 10,
        /*r5*/  0,  0,  6, 12, 16, 12,  6,  0,  0,
        /*r6*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r7*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r8*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
        /*r9*/  0,  0,  0,  0,  0,  0,  0,  0,  0,
    };

    private static readonly int[][] Tables;

    static PieceSquareTables()
    {
        // Index by PieceType enum value (0=None, 1=King, ..., 7=Pawn)
        Tables = new int[8][];
        Tables[0] = new int[90]; // None — unused
        Tables[(int)PieceType.King] = KingTable;
        Tables[(int)PieceType.Advisor] = AdvisorTable;
        Tables[(int)PieceType.Elephant] = ElephantTable;
        Tables[(int)PieceType.Horse] = HorseTable;
        Tables[(int)PieceType.Rook] = RookTable;
        Tables[(int)PieceType.Cannon] = CannonTable;
        Tables[(int)PieceType.Pawn] = PawnTable;
    }

    public static int GetScore(PieceType type, PieceColor color, int index)
    {
        if (type == PieceType.None) return 0;

        int lookupIndex = color == PieceColor.Red ? index : 89 - index;
        return Tables[(int)type][lookupIndex];
    }
}
