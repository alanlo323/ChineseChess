using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Nnue.Features;

namespace ChineseChess.Tests.Infrastructure.Nnue;

/// <summary>
/// 驗證 HalfKAv2Tables 預計算常數表的正確性。
///
/// 重要座標約定：
///   C# index = row * 9 + col（row 0 = 黑方上側，row 9 = 紅方下側）
///   Pikafish sq = rank * 9 + file（rank 0 = 紅方下側，rank 9 = 黑方上側）
///   轉換：pf_sq = (9 - row) * 9 + col
/// </summary>
public class HalfKAv2TablesTests
{
    // ── ValidBB 驗證 ─────────────────────────────────────────────────────

    [Fact]
    public void ValidBB_TotalCount_Equals689()
    {
        // Pikafish PS_NB = 689，遍歷 Pikafish 順序驗證
        int total = 0;
        for (int p = 0; p < HalfKAv2Tables.AllPiecesNb; p++)
        {
            for (int pfSq = 0; pfSq < 90; pfSq++)
            {
                int csIdx = HalfKAv2Tables.CsToCsFromPf(pfSq);
                if (HalfKAv2Tables.ValidBB[p, csIdx]) total++;
            }
        }
        Assert.Equal(HalfKAv2Tables.PsNb, total);
    }

    [Theory]
    [InlineData(0, 90)]   // W_ROOK：全盤 90 格
    [InlineData(1, 5)]    // W_ADVISOR：紅仕 5 格
    [InlineData(2, 90)]   // W_CANNON：全盤
    [InlineData(3, 55)]   // W_PAWN：55 格
    [InlineData(4, 90)]   // W_KNIGHT：全盤
    [InlineData(5, 7)]    // W_BISHOP：紅象 7 格
    [InlineData(6, 6)]    // W_KING：紅王 6 格（D/E 列 × 3 行）
    [InlineData(7, 90)]   // B_ROOK
    [InlineData(8, 5)]    // B_ADVISOR：黑士 5 格
    [InlineData(9, 90)]   // B_CANNON
    [InlineData(10, 55)]  // B_PAWN
    [InlineData(11, 90)]  // B_KNIGHT
    [InlineData(12, 7)]   // B_BISHOP：黑象 7 格
    [InlineData(13, 9)]   // B_KING：黑王 9 格（D/E/F 列 × 3 行）
    public void ValidBB_PieceCount_Correct(int pieceIdx, int expectedCount)
    {
        int count = 0;
        for (int i = 0; i < 90; i++)
            if (HalfKAv2Tables.ValidBB[pieceIdx, i]) count++;
        Assert.Equal(expectedCount, count);
    }

    // ── PSQOffsets 驗證 ──────────────────────────────────────────────────

    [Fact]
    public void PSQOffsets_ValidSquares_HaveNonNegativeOffset()
    {
        for (int p = 0; p < HalfKAv2Tables.AllPiecesNb; p++)
            for (int i = 0; i < 90; i++)
            {
                if (HalfKAv2Tables.ValidBB[p, i])
                    Assert.True(HalfKAv2Tables.PSQOffsets[p, i] >= 0,
                        $"piece={p}, csIdx={i} 應有非負 PSQOffset");
                else
                    Assert.Equal(-1, HalfKAv2Tables.PSQOffsets[p, i]);
            }
    }

    [Fact]
    public void PSQOffsets_MaxValue_Equals688()
    {
        short maxOffset = -1;
        for (int p = 0; p < HalfKAv2Tables.AllPiecesNb; p++)
            for (int i = 0; i < 90; i++)
                if (HalfKAv2Tables.PSQOffsets[p, i] > maxOffset)
                    maxOffset = HalfKAv2Tables.PSQOffsets[p, i];
        Assert.Equal(HalfKAv2Tables.PsNb - 1, maxOffset);
    }

    [Fact]
    public void PSQOffsets_Offsets_AreUnique_PerPiece()
    {
        // 每個棋子類型的有效格偏移必須唯一（無重複）
        for (int p = 0; p < HalfKAv2Tables.AllPiecesNb; p++)
        {
            var seen = new HashSet<short>();
            for (int i = 0; i < 90; i++)
            {
                short offset = HalfKAv2Tables.PSQOffsets[p, i];
                if (offset < 0) continue;
                Assert.True(seen.Add(offset), $"piece={p} 在 csIdx={i} 的偏移 {offset} 重複");
            }
        }
    }

    // ── KingBuckets 驗證 ─────────────────────────────────────────────────

    [Theory]
    // 紅方王位（row 9 = rank 0）
    [InlineData(84, 0, false)]  // D0 = row9,col3 → bucket 0, no mirror
    [InlineData(85, 1, false)]  // E0 = row9,col4 → bucket 1, no mirror
    [InlineData(86, 0, true)]   // F0 = row9,col5 → bucket 0, mirror
    [InlineData(75, 2, false)]  // D1 = row8,col3 → bucket 2
    [InlineData(76, 3, false)]  // E1 = row8,col4 → bucket 3
    [InlineData(77, 2, true)]   // F1 = row8,col5 → bucket 2, mirror
    [InlineData(66, 4, false)]  // D2 = row7,col3 → bucket 4
    [InlineData(67, 5, false)]  // E2 = row7,col4 → bucket 5
    [InlineData(68, 4, true)]   // F2 = row7,col5 → bucket 4, mirror
    // 黑方王位（row 0 = rank 9）
    [InlineData(3, 0, false)]   // D9 = row0,col3 → bucket 0
    [InlineData(4, 1, false)]   // E9 = row0,col4 → bucket 1
    [InlineData(5, 0, true)]    // F9 = row0,col5 → mirror
    [InlineData(13, 3, false)]  // E8 = row1,col4 → bucket 3
    [InlineData(22, 5, false)]  // E7 = row2,col4 → bucket 5
    public void KingBuckets_PalaceSquares_CorrectBucketAndMirror(
        int csIdx, int expectedBucket, bool expectedMirror)
    {
        var (bucket, mirror) = HalfKAv2Tables.GetKingBucketRaw(csIdx);
        Assert.Equal(expectedBucket, bucket);
        Assert.Equal(expectedMirror, mirror);
    }

    // ── IndexMap 驗證 ────────────────────────────────────────────────────

    [Fact]
    public void IndexMap_NoTransform_IsIdentity()
    {
        for (int s = 0; s < 90; s++)
            Assert.Equal(s, HalfKAv2Tables.IndexMap[0, 0, s]);
    }

    [Theory]
    [InlineData(0, 8)]    // row0,col0 → flip_file → row0,col8
    [InlineData(8, 0)]    // row0,col8 → flip_file → row0,col0
    [InlineData(5 * 9 + 4, 5 * 9 + 4)]  // row5,col4(E) → flip_file → row5,col4 (中線不動)
    public void IndexMap_FlipFile_CorrectTransform(int csIdx, int expectedTransformed)
    {
        Assert.Equal(expectedTransformed, HalfKAv2Tables.IndexMap[1, 0, csIdx]);
    }

    [Theory]
    [InlineData(0 * 9 + 0, 9 * 9 + 0)]  // row0,col0 → flip_rank → row9,col0
    [InlineData(9 * 9 + 4, 0 * 9 + 4)]  // row9,col4 → flip_rank → row0,col4
    public void IndexMap_FlipRank_CorrectTransform(int csIdx, int expectedTransformed)
    {
        Assert.Equal(expectedTransformed, HalfKAv2Tables.IndexMap[0, 1, csIdx]);
    }

    // ── ToAllPiecesIndex 驗證 ────────────────────────────────────────────

    [Theory]
    [InlineData(PieceColor.Red, PieceType.Rook, PieceColor.Red, 0)]    // 自己的車 → W_ROOK
    [InlineData(PieceColor.Red, PieceType.Rook, PieceColor.Black, 7)]  // 敵方的車 → B_ROOK
    [InlineData(PieceColor.Black, PieceType.King, PieceColor.Black, 6)] // 自己的王 → W_KING(6)
    [InlineData(PieceColor.Red, PieceType.Elephant, PieceColor.Red, 5)] // 自己的象 → W_BISHOP(5)
    public void ToAllPiecesIndex_Correct(
        PieceColor pieceColor, PieceType pieceType, PieceColor perspective, int expectedIdx)
    {
        Assert.Equal(expectedIdx, HalfKAv2Tables.ToAllPiecesIndex(pieceColor, pieceType, perspective));
    }

    // ── 座標轉換往返一致性 ───────────────────────────────────────────────

    [Fact]
    public void CoordinateConversion_RoundTrip_IsIdentity()
    {
        for (int csIdx = 0; csIdx < 90; csIdx++)
        {
            int pfSq = HalfKAv2Tables.PfFromCs(csIdx);
            int backToCs = HalfKAv2Tables.CsToCsFromPf(pfSq);
            Assert.Equal(csIdx, backToCs);
        }
    }
}
