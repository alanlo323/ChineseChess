using System.Buffers;
using ChineseChess.Application.Configuration;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Nnue.Helpers;

namespace ChineseChess.Infrastructure.AI.Nnue.Network;

/// <summary>
/// 純 C# NNUE 推論實作（純量，不含 SIMD）。
///
/// 網路架構（Big Net）：
///   FT      : HalfKAv2 稀疏特徵 → int16[2×1024] 累加器
///   FT out  : pairwise_mul_clamp → uint8[1024]
///   FC0     : AffineTransformSparseInput：uint8[1024] → int32[16]
///   SqrCReLU + CReLU → uint8[30]
///   FC1     : AffineTransform：uint8[30] → int32[32]
///   CReLU   : → uint8[32]
///   FC2     : AffineTransform：uint8[32] → int32[1]
///   output  : (psqt + positional) / OutputScale
/// </summary>
public sealed class NnueNetwork : INnueNetwork
{
    // ── Pikafish 數值常數 ─────────────────────────────────────────────
    private const int OutputScale     = 16;
    private const int WeightScaleBits = 6;
    private const int L1              = NnueFileFormat.HalfDimensions;  // 1024
    private const int L2              = NnueFileFormat.L2;              // 15
    private const int L3              = NnueFileFormat.L3;              // 32
    private const int PsqtBuckets     = NnueFileFormat.PsqtBuckets;     // 16

    // ── 載入狀態 ─────────────────────────────────────────────────────
    // volatile 確保多執行緒環境下讀取/寫入的可見性（SearchWorker 執行緒讀取，UI 執行緒寫入）
    private volatile NnueWeights? weights;
    private NnueModelInfo? modelInfo;

    public bool IsLoaded => weights is not null;

    public NnueModelInfo? ModelInfo => modelInfo;

    public NnueWeights? Weights => weights;

    // ── 載入 / 卸載 ──────────────────────────────────────────────────

    public async Task LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"NNUE 模型檔不存在：{filePath}");

        var loaded = await Task.Run(() => NnueFileFormat.LoadWeights(filePath), cancellationToken);

        // 先建立 ModelInfo，再更新 weights，讓讀端看到 weights 時 ModelInfo 已就緒
        modelInfo = new NnueModelInfo
        {
            FilePath      = filePath,
            Description   = loaded.Description,
            FileSizeBytes = fileInfo.Length,
            LoadedAt      = DateTime.Now,
        };
        weights = loaded;
    }

    /// <summary>
    /// 從已快取的 NnueWeights 物件直接設定（不重新讀檔）。
    /// 供 LoadedNnueModelRegistry 共享權重時使用，節省重複載入 ~17MB 記憶體。
    /// </summary>
    public void LoadFromWeights(NnueWeights sharedWeights, NnueModelInfo info)
    {
        // 先設定 modelInfo 再設定 weights，確保讀端在看到 weights != null 時 ModelInfo 已就緒
        modelInfo = info;
        weights   = sharedWeights;
    }

    public void Unload()
    {
        weights   = null;
        modelInfo = null;
    }

    // ── 推論 ─────────────────────────────────────────────────────────

    public int Evaluate(IBoard board, NnueAccumulator accumulator)
    {
        if (weights is null)
            throw new InvalidOperationException("NNUE 模型尚未載入");

        int usPerspColorIdx = board.Turn == PieceColor.Red ? 0 : 1;

        // Layer Stack bucket（依雙方大子數）
        int stackBucket = LayerStackBucketHelper.GetBucket(board);
        var stack = weights.Stacks[stackBucket];

        // 暫存緩衝區從 ArrayPool 租用，避免推論熱路徑每次分配觸發 GC
        const int ac0Size = 32;  // CeilToMultiple(L2 * 2, 32)
        byte[] ftOutRented = ArrayPool<byte>.Shared.Rent(L1);
        int[]  fc0Rented   = ArrayPool<int>.Shared.Rent(L2 + 1);
        byte[] ac0Rented   = ArrayPool<byte>.Shared.Rent(ac0Size);
        int[]  fc1Rented   = ArrayPool<int>.Shared.Rent(L3);
        byte[] ac1Rented   = ArrayPool<byte>.Shared.Rent(L3);
        try
        {
        // 1. Feature Transformer output → uint8[1024]
        accumulator.Transform(usPerspColorIdx, ftOutRented);

        // 2. FC0 前向傳播
        PropagateFC0(ftOutRented, stack, fc0Rented);

        // 3. PSQT 附加輸出（fc0Out[L2]，不經後續層）
        int fwdOut = fc0Rented[L2] * (600 * OutputScale) / (127 * (1 << WeightScaleBits));

        // 4. SqrClippedReLU（fc0Out[0..L2-1]）+ ClippedReLU → 合併
        for (int i = 0; i < L2; i++)
        {
            long v = fc0Rented[i];
            ac0Rented[i]      = (byte)Math.Min(127, v * v >> (2 * WeightScaleBits + 7));  // SqrCReLU
            ac0Rented[i + L2] = (byte)Math.Clamp(fc0Rented[i] >> WeightScaleBits, 0, 127);  // CReLU
        }

        // 5. FC1 前向傳播
        PropagateFC1(ac0Rented, stack, fc1Rented);

        // 6. ClippedReLU
        for (int i = 0; i < L3; i++)
            ac1Rented[i] = (byte)Math.Clamp(fc1Rented[i] >> WeightScaleBits, 0, 127);

        // 7. FC2 前向傳播
        int fc2Out = PropagateFC2(ac1Rented, stack);

        // 8. PSQT 分數
        int psqt = accumulator.GetPsqtValue(usPerspColorIdx, stackBucket);

        // 9. 合併：(psqt + positional) / OutputScale
        return psqt / OutputScale + (fc2Out + fwdOut) / OutputScale;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ftOutRented);
            ArrayPool<int>.Shared.Return(fc0Rented);
            ArrayPool<byte>.Shared.Return(ac0Rented);
            ArrayPool<int>.Shared.Return(fc1Rented);
            ArrayPool<byte>.Shared.Return(ac1Rented);
        }
    }

    // ── FC 層推論 ────────────────────────────────────────────────────

    private static void PropagateFC0(byte[] input, NetworkStack stack, int[] output)
    {
        // AffineTransformSparseInput<1024, L2+1=16>
        // weights[inputIdx + outputIdx * 1024]
        int outDims = L2 + 1;
        stack.Fc0Biases.AsSpan().CopyTo(output);

        for (int i = 0; i < L1; i++)
        {
            if (input[i] == 0) continue;
            int v = input[i];
            for (int j = 0; j < outDims; j++)
                output[j] += stack.Fc0Weights[i + j * L1] * v;
        }
    }

    private static void PropagateFC1(byte[] input, NetworkStack stack, int[] output)
    {
        // AffineTransform<30, 32>，padded input = 32
        const int inDims = L2 * 2;           // 30
        stack.Fc1Biases.AsSpan().CopyTo(output);

        const int paddedIn = 32;
        for (int i = 0; i < inDims; i++)
        {
            if (input[i] == 0) continue;
            int v = input[i];
            for (int j = 0; j < L3; j++)
                output[j] += stack.Fc1Weights[i + j * paddedIn] * v;
        }
    }

    private static int PropagateFC2(byte[] input, NetworkStack stack)
    {
        // AffineTransform<32, 1>，padded input = 32
        int output = stack.Fc2Biases[0];
        for (int i = 0; i < L3; i++)
            output += stack.Fc2Weights[i] * input[i];
        return output;
    }

    private static int CeilToMultiple(int n, int b) => (n + b - 1) / b * b;
}
