using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Nnue.Features;
using ChineseChess.Infrastructure.AI.Nnue.Helpers;
using ChineseChess.Infrastructure.AI.Nnue.Network;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// <see cref="TrainingNetwork"/> 的推論視圖：共享 weights 陣列引用（唯讀），
/// 持有獨立的前向快取，可安全從多個執行緒同時使用。
///
/// 設計原則：
///   - weights 陣列（FtWeights 等）在生成對局階段不會被寫入，因此多個 InferenceView
///     同時唯讀存取是安全的。
///   - 每個 InferenceView 持有自己的 cached* 欄位（約 10 KB），完全無競爭。
///   - 只實作推論路徑（EvaluateToScore），不含梯度或 Adam 狀態。
///
/// 用法：為每個並行對局任務建立一個獨立的 InferenceView 實例，透過
/// <see cref="TrainingNetworkEvaluator"/> 包裝後傳遞給 <see cref="Search.SearchEngine"/>。
/// </summary>
internal sealed class TrainingNetworkInferenceView
{
    // ── 推論常數（對應 NnueNetwork / TrainingNetwork）──────────────────
    private const int L1          = NnueFileFormat.HalfDimensions;   // 1024
    private const int L2          = NnueFileFormat.L2;               // 15
    private const int L3          = NnueFileFormat.L3;               // 32
    private const int PsqtBuckets = NnueFileFormat.PsqtBuckets;      // 16

    private const float OutputScale          = 16f;
    private const float WeightScaleBitsF     = 64f;
    private const float PairwiseClampDivisor = 512f;
    private const float PairwiseClampMax     = 255f;
    private const float WdlScaleFactor       = 600f;
    private const float SqrCReLUExtraDivisor = 128f;
    private const float FtBiasScale          = 2f;
    private const float Int8Max              = 127f;

    // 每個視角的最大活躍特徵數（象棋最多 14 個非將帥棋子，預留 2 倍餘裕）
    private const int MaxFeaturesPerPerspective = 32;

    // ── 共享 weights（唯讀引用，不複製資料）────────────────────────────
    private readonly float[]   ftBiases;
    private readonly float[]   ftWeights;
    private readonly float[]   ftPsqt;
    private readonly float[][] fc0Biases;
    private readonly float[][] fc0Weights;
    private readonly float[][] fc1Biases;
    private readonly float[][] fc1Weights;
    private readonly float[][] fc2Biases;
    private readonly float[][] fc2Weights;

    // ── 每個 View 獨立的前向快取（約 10 KB，執行緒本地）────────────────
    private readonly float[,] cachedFtAcc  = new float[2, L1];
    private readonly float[]  cachedFtOut  = new float[L1];
    private readonly float[]  cachedFc0Out = new float[L2 + 1];
    private readonly float[]  cachedAc0    = new float[32];
    private readonly float[]  cachedFc1Out = new float[L3];
    private readonly float[]  cachedAc1    = new float[L3];

    // ComputeFtAccumulator 快取特徵索引（供 ComputePsqt 重用）
    private int   cachedFeatCount0;
    private int   cachedFeatCount1;
    private readonly int[] cachedFeats0   = new int[MaxFeaturesPerPerspective];
    private readonly int[] cachedFeats1   = new int[MaxFeaturesPerPerspective];
    private readonly int[] featuresWork   = new int[MaxFeaturesPerPerspective];  // GetActiveFeatures 工作緩衝

    /// <summary>
    /// 建立推論視圖，直接引用 <paramref name="network"/> 的 weights 陣列（零複製）。
    /// </summary>
    public TrainingNetworkInferenceView(TrainingNetwork network)
    {
        ftBiases   = network.FtBiases;
        ftWeights  = network.FtWeights;
        ftPsqt     = network.FtPsqt;
        fc0Biases  = network.Fc0Biases;
        fc0Weights = network.Fc0Weights;
        fc1Biases  = network.Fc1Biases;
        fc1Weights = network.Fc1Weights;
        fc2Biases  = network.Fc2Biases;
        fc2Weights = network.Fc2Weights;
    }

    /// <summary>
    /// 純前向推論，回傳 centipawn 分數（先手視角）。
    /// 可從多個執行緒同時安全呼叫（各自持有獨立的快取陣列）。
    /// </summary>
    public int EvaluateToScore(IBoard board)
    {
        int usPerspColorIdx = board.Turn == PieceColor.Red ? 0 : 1;
        int stackBucket     = LayerStackBucketHelper.GetBucket(board);

        ComputeFtAccumulator(board, cachedFtAcc);
        var ftAcc = cachedFtAcc;

        var ftOut = cachedFtOut;
        for (int p = 0; p < 2; p++)
        {
            int colorIdx  = p == 0 ? usPerspColorIdx : 1 - usPerspColorIdx;
            int outOffset = p * (L1 / 2);
            for (int j = 0; j < L1 / 2; j++)
            {
                float a0 = Math.Clamp(ftAcc[colorIdx, j],          0f, PairwiseClampMax);
                float a1 = Math.Clamp(ftAcc[colorIdx, j + L1 / 2], 0f, PairwiseClampMax);
                ftOut[outOffset + j] = a0 * a1 / PairwiseClampDivisor;
            }
        }

        var fc0Out = cachedFc0Out;
        for (int j = 0; j <= L2; j++)
        {
            float sum = fc0Biases[stackBucket][j];
            for (int i = 0; i < L1; i++)
                sum += fc0Weights[stackBucket][i + j * L1] * ftOut[i];
            fc0Out[j] = sum;
        }

        // WDL PSQT 分量來自 fc0Out[L2]，以與主評估路徑合併
        float psqtFrac = fc0Out[L2] * WdlScaleFactor * OutputScale / (Int8Max * WeightScaleBitsF);

        var ac0 = cachedAc0;
        for (int i = 0; i < L2; i++)
        {
            float v = fc0Out[i];
            ac0[i]      = Math.Clamp(v * v / (WeightScaleBitsF * WeightScaleBitsF * SqrCReLUExtraDivisor), 0f, Int8Max);
            ac0[i + L2] = Math.Clamp(v / WeightScaleBitsF, 0f, Int8Max);
        }

        var fc1Out = cachedFc1Out;
        for (int j = 0; j < L3; j++)
        {
            float sum = fc1Biases[stackBucket][j];
            for (int i = 0; i < 32; i++)
                sum += fc1Weights[stackBucket][i + j * 32] * ac0[i];
            fc1Out[j] = sum;
        }

        var ac1 = cachedAc1;
        for (int i = 0; i < L3; i++)
            ac1[i] = Math.Clamp(fc1Out[i] / WeightScaleBitsF, 0f, Int8Max);

        float fc2Out = fc2Biases[stackBucket][0];
        for (int i = 0; i < L3; i++)
            fc2Out += fc2Weights[stackBucket][i] * ac1[i];

        float psqt = ComputePsqt(usPerspColorIdx, stackBucket);

        float positional    = (fc2Out + psqtFrac) / OutputScale;
        float predictedScore = psqt / OutputScale + positional;

        return (int)predictedScore;
    }

    // ── 私有輔助（與 TrainingNetwork 相同邏輯，寫入本 View 的快取欄位）──

    private void ComputeFtAccumulator(IBoard board, float[,] acc)
    {
        for (int c = 0; c < 2; c++)
        {
            var perspective = c == 0 ? PieceColor.Red : PieceColor.Black;

            // FT biases（×FtBiasScale 還原縮放）
            for (int n = 0; n < L1; n++) acc[c, n] = ftBiases[n] * FtBiasScale;

            bool mm    = MidMirrorEncoder.RequiresMidMirror(board, perspective);
            int  count = HalfKAv2Features.GetActiveFeatures(board, perspective, mm, featuresWork);

            // 快取特徵索引供 ComputePsqt 重用
            Debug.Assert(count <= MaxFeaturesPerPerspective,
                $"GetActiveFeatures 回傳 {count} 個特徵，超過緩衝大小 {MaxFeaturesPerPerspective}");
            if (c == 0) { cachedFeatCount0 = count; featuresWork.AsSpan(0, count).CopyTo(cachedFeats0); }
            else        { cachedFeatCount1 = count; featuresWork.AsSpan(0, count).CopyTo(cachedFeats1); }

            Span<float> accRow = MemoryMarshal.CreateSpan(ref acc[c, 0], L1);
            for (int f = 0; f < count; f++)
            {
                int wOffset = (int)((long)featuresWork[f] * L1);
                AddFloatVector(accRow, ftWeights, wOffset);
            }
        }
    }

    private float ComputePsqt(int usPerspColorIdx, int stackBucket)
    {
        float us = 0f, them = 0f;
        for (int c = 0; c < 2; c++)
        {
            int   count = c == 0 ? cachedFeatCount0 : cachedFeatCount1;
            int[] feats = c == 0 ? cachedFeats0 : cachedFeats1;
            float sum   = 0f;
            for (int f = 0; f < count; f++)
            {
                int pOffset = (int)((long)feats[f] * PsqtBuckets + stackBucket);
                sum += ftPsqt[pOffset];
            }
            if (c == usPerspColorIdx) us = sum; else them = sum;
        }
        return (us - them) / 2f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddFloatVectorCore(Span<float> dest, ReadOnlySpan<float> src)
    {
        if (Vector512.IsHardwareAccelerated)
        {
            int stride = Vector512<float>.Count;  // 16
            Debug.Assert(L1 % stride == 0, $"L1={L1} 非 Vector512 stride={stride} 的整數倍，SIMD 迴圈會漏算尾端元素");
            ref float dRef = ref MemoryMarshal.GetReference(dest);
            ref float sRef = ref MemoryMarshal.GetReference(src);
            for (int n = 0; n <= L1 - stride; n += stride)
            {
                var dVec = Vector512.LoadUnsafe(ref Unsafe.Add(ref dRef, n));
                var sVec = Vector512.LoadUnsafe(ref Unsafe.Add(ref sRef, n));
                Vector512.StoreUnsafe(Vector512.Add(dVec, sVec), ref Unsafe.Add(ref dRef, n));
            }
        }
        else
        {
            for (int n = 0; n < L1; n++)
                dest[n] += src[n];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddFloatVector(Span<float> dest, float[] src, int srcOffset)
        => AddFloatVectorCore(dest, src.AsSpan(srcOffset, L1));
}
