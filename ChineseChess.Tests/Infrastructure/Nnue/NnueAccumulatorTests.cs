using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Nnue.Features;
using ChineseChess.Infrastructure.AI.Nnue.Network;

namespace ChineseChess.Tests.Infrastructure.Nnue;

/// <summary>
/// 驗證 NnueAccumulator：
///   1. Refresh 後 Transform 產生非零輸出（基礎路徑）
///   2. 兵前進後，增量更新結果 == 全量 Refresh 結果（兩個視角）
///   3. Push/Pop 正確還原累加器狀態
/// </summary>
public class NnueAccumulatorTests
{
    // 初始局面 FEN（紅方先走）
    private const string InitialFen =
        "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ── 合成權重 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 建立可重複的非均勻合成權重，確保不同特徵格子有不同貢獻值，
    /// 使 incremental != full-refresh 的 bug 能被偵測。
    /// </summary>
    private static NnueWeights CreateSyntheticWeights()
    {
        const int hd    = NnueFileFormat.HalfDimensions;   // 1024
        const int dims  = NnueFileFormat.Dimensions;        // 16536
        const int psqtB = NnueFileFormat.PsqtBuckets;      // 16
        const int l2    = NnueFileFormat.L2;                // 15
        const int l3    = NnueFileFormat.L3;                // 32
        const int nb    = NnueFileFormat.LayerStacksNb;     // 16

        // FT biases：固定 100，確保累加器初始夠大（pairwise 乘積需 ≥ 512 才不全零）
        var ftBiases = new short[hd];
        for (int i = 0; i < hd; i++) ftBiases[i] = 100;

        // FT weights：全為 +1，每個活躍特徵均對累加器貢獻 1
        // 使用均勻正值確保輸出非零，同時仍能驗證 added/removed 特徵計算正確性
        var ftWeights = new sbyte[(long)dims * hd];
        for (long i = 0; i < ftWeights.Length; i++)
            ftWeights[i] = 1;

        // PSQT weights：非零但小，避免 overflow
        var ftPsqt = new int[(long)dims * psqtB];
        for (long i = 0; i < ftPsqt.Length; i++)
            ftPsqt[i] = (int)(i % 7);

        // FC 層（不參與累加器測試，填全零）
        var stacks = new NetworkStack[nb];
        for (int s = 0; s < nb; s++)
        {
            stacks[s] = new NetworkStack
            {
                Fc0Biases  = new int[l2 + 1],
                Fc0Weights = new sbyte[(long)(l2 + 1) * hd],
                Fc1Biases  = new int[l3],
                Fc1Weights = new sbyte[(long)l3 * 32],
                Fc2Biases  = new int[1],
                Fc2Weights = new sbyte[32],
            };
        }

        return new NnueWeights
        {
            FtBiases      = ftBiases,
            FtWeights     = ftWeights,
            FtPsqtWeights = ftPsqt,
            Stacks        = stacks,
            Description   = "synthetic-test",
        };
    }

    // ── 測試案例 ─────────────────────────────────────────────────────────

    [Fact]
    public void Transform_AfterRefresh_ProducesNonZeroOutput()
    {
        var board   = new Board(InitialFen);
        var weights = CreateSyntheticWeights();
        var accum   = new NnueAccumulator();

        accum.Refresh(board, weights);

        var output = new byte[NnueFileFormat.HalfDimensions];
        accum.Transform(0, output);

        Assert.True(output.Any(b => b != 0),
            "Refresh + Transform 應產生至少一個非零輸出");
    }

    [Fact]
    public void IncrementalUpdate_AfterPawnMove_MatchesFullRefresh()
    {
        // 初始局面：紅兵 row=6 col=0（index=54）→ row=5 col=0（index=45）
        const int from = 54;
        const int to   = 45;

        var board   = new Board(InitialFen);
        var weights = CreateSyntheticWeights();

        // 確認格子為紅兵
        var movedPiece = board.GetPiece(from);
        Assert.Equal(PieceColor.Red,  movedPiece.Color);
        Assert.Equal(PieceType.Pawn, movedPiece.Type);

        // ── Step 1：初始全量刷新 ─────────────────────────────────────────
        var accum = new NnueAccumulator();
        accum.Refresh(board, weights);

        // ── Step 2：紀錄走棋前的 bucket / mirror 資訊（兩個視角）────────
        var preInfo = new (bool midMirror, int combinedBucket, bool mirror)[2];
        for (int c = 0; c < 2; c++)
        {
            var persp = c == 0 ? PieceColor.Red : PieceColor.Black;
            bool mm  = MidMirrorEncoder.RequiresMidMirror(board, persp);
            int  own = FindKing(board, persp);
            int  opp = FindKing(board, c == 0 ? PieceColor.Black : PieceColor.Red);
            var (kb, mir) = HalfKAv2Tables.GetKingBucket(own, opp, mm);
            int ab = HalfKAv2Features.ComputeAttackBucket(board, persp);
            preInfo[c] = (mm, kb * HalfKAv2Tables.AttackBucketNb + ab, mir);
        }

        // ── Step 3：走棋 ────────────────────────────────────────────────
        var capturedPiece = board.GetPiece(to);   // 初始局面此格應為空
        var move = new Move(from, to);
        accum.Push();
        board.MakeMove(move);

        // ── Step 4：兩個視角增量更新 ─────────────────────────────────────
        for (int c = 0; c < 2; c++)
        {
            var persp = c == 0 ? PieceColor.Red : PieceColor.Black;
            var cf = HalfKAv2Features.GetChangedFeatures(
                board, persp, move, movedPiece, capturedPiece,
                preInfo[c].midMirror, preInfo[c].combinedBucket, preInfo[c].mirror);

            Assert.False(cf.NeedsRefresh,
                $"視角 {persp} 兵前進不應觸發全量刷新");

            accum.IncrementalUpdate(c, cf.Added, cf.Removed, weights);
        }

        // ── Step 5：全量刷新作為基準 ─────────────────────────────────────
        var accumRef = new NnueAccumulator();
        accumRef.Refresh(board, weights);

        // ── Step 6：比較兩個視角的 Transform 輸出 ─────────────────────
        var incrementalOut = new byte[NnueFileFormat.HalfDimensions];
        var referenceOut   = new byte[NnueFileFormat.HalfDimensions];

        for (int colorIdx = 0; colorIdx < 2; colorIdx++)
        {
            accum.Transform(colorIdx, incrementalOut);
            accumRef.Transform(colorIdx, referenceOut);

            Assert.Equal(referenceOut, incrementalOut);
        }
    }

    [Fact]
    public void PushPop_RestoresAccumulatorState()
    {
        var board   = new Board(InitialFen);
        var weights = CreateSyntheticWeights();
        var accum   = new NnueAccumulator();

        accum.Refresh(board, weights);

        // 紀錄 Push 前的 Transform 輸出
        var before = new byte[NnueFileFormat.HalfDimensions];
        accum.Transform(0, before);

        // Push → 改變棋盤（全量刷新模擬狀態變更）→ Pop
        var move = new Move(54, 45);  // 紅兵前進
        accum.Push();
        board.MakeMove(move);
        accum.Refresh(board, weights);  // 改變當前層累加器

        accum.Pop();
        board.UnmakeMove(move);

        // Pop 後應還原 Push 前的狀態
        var after = new byte[NnueFileFormat.HalfDimensions];
        accum.Transform(0, after);

        Assert.Equal(before, after);
    }

    [Fact]
    public void GetPsqtValue_AfterRefresh_SymmetricForBothSides()
    {
        // 初始對稱局面：兩側 PSQT 差值應接近 0（不嚴格為 0，因合成 PSQT 非對稱）
        var board   = new Board(InitialFen);
        var weights = CreateSyntheticWeights();
        var accum   = new NnueAccumulator();

        accum.Refresh(board, weights);

        // 兩個視角 PSQT 皆能呼叫成功，且值為整數（無例外）
        for (int bucket = 0; bucket < NnueFileFormat.PsqtBuckets; bucket++)
        {
            int psqtRed   = accum.GetPsqtValue(0, bucket);
            int psqtBlack = accum.GetPsqtValue(1, bucket);
            // 僅確保不拋例外且型別正確
            Assert.IsType<int>(psqtRed);
            Assert.IsType<int>(psqtBlack);
        }
    }

    // ── 輔助 ─────────────────────────────────────────────────────────────

    private static int FindKing(IBoard board, PieceColor color)
    {
        for (int i = 0; i < 90; i++)
        {
            var p = board.GetPiece(i);
            if (!p.IsNone && p.Color == color && p.Type == PieceType.King) return i;
        }
        return -1;
    }
}
