using ChineseChess.Domain.Enums;
using System.Diagnostics;

namespace ChineseChess.Infrastructure.AI.Nnue.Features;

/// <summary>
/// HalfKAv2_hm 特徵的預計算常數表。
/// 所有索引以 C# 慣例排列：index = row * 9 + col，row 0 = 上方（黑方），row 9 = 下方（紅方）。
/// Pikafish 採用 square = rank * 9 + file，rank 0 = 下方（紅方）。
/// 因此雙向轉換：pf_sq = (9 - cs_idx/9) * 9 + cs_idx%9，反向亦然。
/// </summary>
public static class HalfKAv2Tables
{
    // ── 維度常數 ────────────────────────────────────────────────────────────
    public const int PsNb = 689;           // 每個（KingBucket × AttackBucket）組合的特徵數
    public const int KingBucketNb = 6;     // 王位置 bucket 數（宮城內 D/E/F × 3 行）
    public const int AttackBucketNb = 4;   // 進攻 bucket 數（bool(rook)*2 + bool(knight|cannon)）
    public const int TotalBuckets = KingBucketNb * AttackBucketNb;  // = 24
    public const int Dimensions = TotalBuckets * PsNb;              // = 16,536

    // AllPieces 共 14 種（W/B × 7 類），順序同 Pikafish AllPieces
    public const int AllPiecesNb = 14;

    // ── AllPieces 索引（0-13）——以「本方視角」作為 White，對方為 Black ───────
    // 0:W_ROOK  1:W_ADVISOR  2:W_CANNON  3:W_PAWN  4:W_KNIGHT  5:W_BISHOP  6:W_KING
    // 7:B_ROOK  8:B_ADVISOR  9:B_CANNON 10:B_PAWN 11:B_KNIGHT 12:B_BISHOP 13:B_KING

    /// <summary>
    /// C# PieceType (0-7) → AllPieces「本方（White）」索引。
    /// -1 表示 PieceType.None 或 King（King 不加入特徵）。
    /// </summary>
    private static readonly int[] TypeToOwnIdx = [
        -1,  // None = 0
         6,  // King = 1    → W_KING（排除於特徵之外，但需要索引供 ValidBB）
         1,  // Advisor = 2 → W_ADVISOR
         5,  // Elephant = 3→ W_BISHOP
         4,  // Horse = 4   → W_KNIGHT
         0,  // Rook = 5    → W_ROOK
         2,  // Cannon = 6  → W_CANNON
         3,  // Pawn = 7    → W_PAWN
    ];

    // ── RawKingBuckets[90]：(bucket 0-5, mirror) ──────────────────────────
    // 以 C# index 直接查詢，僅宮城格有意義
    // 編碼：低 3 bit = bucket (0-5)，bit 3 = mirror flag（1 = 需鏡像）
    // 與 Pikafish 原始陣列格式相同：M(s) = (1<<3)|s
    private static readonly byte[] RawKingBuckets = BuildRawKingBuckets();

    // ── ValidBB[14][90]：各棋子在各格的有效性 ────────────────────────────
    public static readonly bool[,] ValidBB = BuildValidBB();

    // ── PSQOffsets[14][90]：有效格的累積特徵偏移（無效格為 -1）─────────────
    // 與 Pikafish PSQOffsets 完全相容
    public static readonly short[,] PSQOffsets = BuildPsqOffsets();

    // ── IndexMap[mirror:0/1][rankFlip:0/1][c#idx] → 轉換後 c#idx ─────────
    // mirror=1 表示 flip_file（col → 8-col）
    // rankFlip=1 表示 flip_rank（row → 9-row），BLACK 視角使用
    public static readonly byte[,,] IndexMap = BuildIndexMap();

    // ── 公開查詢方法 ─────────────────────────────────────────────────────

    /// <summary>
    /// 取得指定格的 King Bucket（0-5）與鏡像旗標。
    /// 僅對宮城格（合法王位）有意義。
    /// </summary>
    public static (int Bucket, bool Mirror) GetKingBucketRaw(int cSharpIndex)
    {
        byte raw = RawKingBuckets[cSharpIndex];
        return (raw & 0x7, (raw >> 3) != 0);
    }

    /// <summary>
    /// 考慮雙王位置計算實際 King Bucket 與鏡像。
    /// 完整實作 Pikafish KingBuckets lambda 的邏輯。
    /// </summary>
    public static (int Bucket, bool Mirror) GetKingBucket(int ownKingSqCS, int enemyKingSqCS, bool midMirror)
    {
        byte ownRaw = RawKingBuckets[ownKingSqCS];
        byte enemyRaw = RawKingBuckets[enemyKingSqCS];

        int ownBucket = ownRaw & 0x7;
        int enemyBucket = enemyRaw & 0x7;
        bool ownOnF = (ownRaw >> 3) != 0;
        bool enemyOnF = (enemyRaw >> 3) != 0;

        bool mirror = ownOnF
            || ((ownBucket & 1) != 0   // 己王在 E 列（奇數 bucket）
                && (enemyOnF || (((enemyBucket & 1) != 0) && midMirror)));

        return (ownBucket, mirror);
    }

    /// <summary>
    /// C# (PieceColor, PieceType) → AllPieces 索引（0-13），
    /// perspective 決定哪方視角（本方 → White=0-6，對方 → Black=7-13）。
    /// </summary>
    public static int ToAllPiecesIndex(PieceColor pieceColor, PieceType pieceType, PieceColor perspective)
    {
        int typeIdx = TypeToOwnIdx[(int)pieceType];
        if (typeIdx < 0) return -1;
        bool isOwn = pieceColor == perspective;
        return isOwn ? typeIdx : typeIdx + 7;
    }

    // ── 私有建構方法 ─────────────────────────────────────────────────────

    /// <summary>
    /// 建立 C# 索引 → (bucket, mirror) 的 raw 編碼表。
    /// Pikafish KingBuckets[pf_sq] 同樣以 rank*9+file 排列，只需翻轉 rank 即可對應。
    /// </summary>
    private static byte[] BuildRawKingBuckets()
    {
        // Pikafish 原始 KingBuckets 陣列：以 pf_sq = rank*9+file 排列，rank 0 = 下（紅方）
        // M(s) = (1 << 3) | s
        var pfBuckets = new byte[90];

        // Pikafish rank-0（C# row-9，紅方底行）：D0=4, E0=1, F0=M(0)=8
        // 對應 pf_sq: A0=0,B0=1,...I0=8（rank 0, files A-I）
        pfBuckets[0 * 9 + 3] = 0;          // D0: bucket 0
        pfBuckets[0 * 9 + 4] = 1;          // E0: bucket 1
        pfBuckets[0 * 9 + 5] = (1 << 3);   // F0: mirror + bucket 0

        // rank-1（C# row-8）
        pfBuckets[1 * 9 + 3] = 2;
        pfBuckets[1 * 9 + 4] = 3;
        pfBuckets[1 * 9 + 5] = (1 << 3) | 2;

        // rank-2（C# row-7）
        pfBuckets[2 * 9 + 3] = 4;
        pfBuckets[2 * 9 + 4] = 5;
        pfBuckets[2 * 9 + 5] = (1 << 3) | 4;

        // ranks 3-6：全為 0（非宮城）
        // rank-7（C# row-2）
        pfBuckets[7 * 9 + 3] = 4;
        pfBuckets[7 * 9 + 4] = 5;
        pfBuckets[7 * 9 + 5] = (1 << 3) | 4;

        // rank-8（C# row-1）
        pfBuckets[8 * 9 + 3] = 2;
        pfBuckets[8 * 9 + 4] = 3;
        pfBuckets[8 * 9 + 5] = (1 << 3) | 2;

        // rank-9（C# row-0，黑方頂行）
        pfBuckets[9 * 9 + 3] = 0;
        pfBuckets[9 * 9 + 4] = 1;
        pfBuckets[9 * 9 + 5] = (1 << 3);

        // 將 Pikafish 索引轉為 C# 索引
        var csBuckets = new byte[90];
        for (int pfSq = 0; pfSq < 90; pfSq++)
        {
            int csIdx = CsToCsFromPf(pfSq);
            csBuckets[csIdx] = pfBuckets[pfSq];
        }
        return csBuckets;
    }

    private static bool[,] BuildValidBB()
    {
        var v = new bool[AllPiecesNb, 90];

        for (int csIdx = 0; csIdx < 90; csIdx++)
        {
            int row = csIdx / 9, col = csIdx % 9;

            // 判斷各 bitboard 條件（以 C# row/col 計算）
            bool redHalf = row >= 5;                 // Pikafish Rank 0-4（紅方半場）
            bool blackHalf = row <= 4;               // Pikafish Rank 5-9（黑方半場）
            bool pawnFile = col is 0 or 2 or 4 or 6 or 8; // A,C,E,G,I 奇數列

            // 0:W_ROOK  → 全盤
            v[0, csIdx] = true;

            // 1:W_ADVISOR → (Rank0∪Rank2)∩(FileD∪FileF) ∪ Rank1∩FileE（紅仕位置）
            v[1, csIdx] = (row == 9 || row == 7) && (col == 3 || col == 5)
                        || row == 8 && col == 4;

            // 2:W_CANNON → 全盤
            v[2, csIdx] = true;

            // 3:W_PAWN → HalfBB[BLACK] ∪ (Rank3∪Rank4)∩PawnFile
            //   HalfBB[BLACK] = Pikafish ranks 5-9 = C# rows 0-4
            //   Rank3 = C# row 6, Rank4 = C# row 5
            v[3, csIdx] = blackHalf || ((row == 6 || row == 5) && pawnFile);

            // 4:W_KNIGHT → 全盤
            v[4, csIdx] = true;

            // 5:W_BISHOP → (Rank0∪Rank4)∩(FileC∪FileG) ∪ Rank2∩(FileA∪FileE∪FileI)
            //   Rank0=row9, Rank4=row5, Rank2=row7
            v[5, csIdx] = (row == 9 || row == 5) && (col == 2 || col == 6)
                        || row == 7 && (col == 0 || col == 4 || col == 8);

            // 6:W_KING → HalfBB[WHITE]∩Palace∩~FileF = rows 7-9, cols 3-4
            v[6, csIdx] = row >= 7 && (col == 3 || col == 4);

            // 7:B_ROOK → 全盤
            v[7, csIdx] = true;

            // 8:B_ADVISOR → (Rank7∪Rank9)∩(FileD∪FileF) ∪ Rank8∩FileE（黑士位置）
            //   Rank7=row2, Rank9=row0, Rank8=row1
            v[8, csIdx] = (row == 2 || row == 0) && (col == 3 || col == 5)
                        || row == 1 && col == 4;

            // 9:B_CANNON → 全盤
            v[9, csIdx] = true;

            // 10:B_PAWN → HalfBB[WHITE] ∪ (Rank6∪Rank5)∩PawnFile
            //   HalfBB[WHITE] = Pikafish ranks 0-4 = C# rows 5-9
            //   Rank6=row3, Rank5=row4
            v[10, csIdx] = redHalf || ((row == 3 || row == 4) && pawnFile);

            // 11:B_KNIGHT → 全盤
            v[11, csIdx] = true;

            // 12:B_BISHOP → (Rank5∪Rank9)∩(FileC∪FileG) ∪ Rank7∩(FileA∪FileE∪FileI)
            //   Rank5=row4, Rank9=row0, Rank7=row2
            v[12, csIdx] = (row == 4 || row == 0) && (col == 2 || col == 6)
                         || row == 2 && (col == 0 || col == 4 || col == 8);

            // 13:B_KING → HalfBB[BLACK]∩Palace = rows 0-2, cols 3-5
            v[13, csIdx] = row <= 2 && col >= 3 && col <= 5;
        }

        // 驗證 PS_NB
        int total = 0;
        for (int p = 0; p < AllPiecesNb; p++)
            for (int pfSq = 0; pfSq < 90; pfSq++)
                if (v[p, CsToCsFromPf(pfSq)]) total++;
        Debug.Assert(total == PsNb, $"ValidBB 總計 {total}，預期 {PsNb}");

        return v;
    }

    private static short[,] BuildPsqOffsets()
    {
        var offsets = new short[AllPiecesNb, 90];
        for (int p = 0; p < AllPiecesNb; p++)
            for (int s = 0; s < 90; s++)
                offsets[p, s] = -1;

        int cumOffset = 0;
        // 以 Pikafish 迭代順序：先 AllPieces，再 pf_sq = 0..89（rank 0 = 下方，file A 到 I）
        for (int p = 0; p < AllPiecesNb; p++)
        {
            for (int pfSq = 0; pfSq < 90; pfSq++)
            {
                int csIdx = CsToCsFromPf(pfSq);
                if (ValidBB[p, csIdx])
                    offsets[p, csIdx] = (short)cumOffset++;
            }
        }
        Debug.Assert(cumOffset == PsNb, $"PSQOffsets 計數 {cumOffset}，預期 {PsNb}");
        return offsets;
    }

    private static byte[,,] BuildIndexMap()
    {
        var map = new byte[2, 2, 90];
        for (int m = 0; m < 2; m++)
        {
            for (int r = 0; r < 2; r++)
            {
                for (int s = 0; s < 90; s++)
                {
                    int row = s / 9, col = s % 9;
                    if (m == 1) col = 8 - col;   // flip_file（水平鏡像）
                    if (r == 1) row = 9 - row;   // flip_rank（上下翻轉）
                    map[m, r, s] = (byte)(row * 9 + col);
                }
            }
        }
        return map;
    }

    /// <summary>將 Pikafish square（rank*9+file）轉為 C# index（(9-rank)*9+file）。</summary>
    internal static int CsToCsFromPf(int pfSq)
    {
        int rank = pfSq / 9, file = pfSq % 9;
        return (9 - rank) * 9 + file;
    }

    /// <summary>將 C# index 轉為 Pikafish square。</summary>
    internal static int PfFromCs(int csIdx)
    {
        int row = csIdx / 9, col = csIdx % 9;
        return (9 - row) * 9 + col;
    }
}
