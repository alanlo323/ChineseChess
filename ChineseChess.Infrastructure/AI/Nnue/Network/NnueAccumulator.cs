using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Nnue.Features;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ChineseChess.Infrastructure.AI.Nnue.Network;

/// <summary>
/// 雙視角 HalfKAv2 特徵轉換器累加器。
///
/// 每個視角維護一個 int16[1024] 累加向量（初始化為 FT 偏置），
/// 當棋子移動時透過增量更新（Phase 4）避免全量重算。
///
/// 「堆疊」設計：搜尋推進時 Push，回退時 Pop，確保搜尋樹各節點
/// 累加器狀態一致（搜尋深度上限 = MaxDepth）。
///
/// 熱路徑（Refresh / IncrementalUpdate）使用 Vector512&lt;short&gt; 向量化：
///   1024 shorts / 32（AVX-512BW lane 數）= 32 次迭代（原 1024 次）。
/// Transform 使用 Vector256&lt;int&gt; 向量化成對乘法（AVX2）。
/// 非 SIMD 機器自動退回純量路徑。
/// </summary>
public sealed class NnueAccumulator
{
    public const int HalfDimensions = NnueFileFormat.HalfDimensions;
    public const int PsqtBuckets    = NnueFileFormat.PsqtBuckets;
    private const int MaxDepth = 128;

    // ── 堆疊狀態 ─────────────────────────────────────────────────────

    // [depth][color][neuron] — 使用 flat 陣列減少 GC 壓力
    private readonly short[] accumFlat;
    private readonly int[] psqtFlat;
    private int depth;

    public NnueAccumulator()
    {
        accumFlat = new short[MaxDepth * 2 * HalfDimensions];
        psqtFlat  = new int[MaxDepth * 2 * PsqtBuckets];
        depth     = 0;
    }

    // ── 目前深度的 span ──────────────────────────────────────────────

    private Span<short> GetAccum(int colorIdx) =>
        accumFlat.AsSpan(depth * 2 * HalfDimensions + colorIdx * HalfDimensions, HalfDimensions);

    private Span<int> GetPsqt(int colorIdx) =>
        psqtFlat.AsSpan(depth * 2 * PsqtBuckets + colorIdx * PsqtBuckets, PsqtBuckets);

    // ── 前一深度（供增量更新複製基準）───────────────────────────────

    private ReadOnlySpan<short> GetPrevAccum(int colorIdx) =>
        accumFlat.AsSpan((depth - 1) * 2 * HalfDimensions + colorIdx * HalfDimensions, HalfDimensions);

    private ReadOnlySpan<int> GetPrevPsqt(int colorIdx) =>
        psqtFlat.AsSpan((depth - 1) * 2 * PsqtBuckets + colorIdx * PsqtBuckets, PsqtBuckets);

    // ── Stack 管理 ───────────────────────────────────────────────────

    /// <summary>走棋前呼叫：將目前狀態複製至新一層。</summary>
    public void Push()
    {
        if (depth + 1 >= MaxDepth)
            throw new InvalidOperationException($"NnueAccumulator 超過最大深度 {MaxDepth}");

        int srcOffset = depth * 2 * HalfDimensions;
        int dstOffset = (depth + 1) * 2 * HalfDimensions;
        accumFlat.AsSpan(srcOffset, 2 * HalfDimensions)
                 .CopyTo(accumFlat.AsSpan(dstOffset, 2 * HalfDimensions));

        int srcPsqt = depth * 2 * PsqtBuckets;
        int dstPsqt = (depth + 1) * 2 * PsqtBuckets;
        psqtFlat.AsSpan(srcPsqt, 2 * PsqtBuckets)
                .CopyTo(psqtFlat.AsSpan(dstPsqt, 2 * PsqtBuckets));

        depth++;
    }

    /// <summary>UnmakeMove 後呼叫：退回上一層。</summary>
    public void Pop()
    {
        if (depth == 0)
            throw new InvalidOperationException("NnueAccumulator Pop 已在底部");
        depth--;
    }

    // ── 全量刷新 ─────────────────────────────────────────────────────

    /// <summary>從棋盤狀態完整重算兩個視角的累加器。</summary>
    public void Refresh(IBoard board, NnueWeights weights)
    {
        var features = new int[32];

        for (int colorIdx = 0; colorIdx < 2; colorIdx++)
        {
            var perspective = colorIdx == 0 ? PieceColor.Red : PieceColor.Black;

            // 初始化為 FT 偏置
            Span<short> accum = GetAccum(colorIdx);
            weights.FtBiases.AsSpan().CopyTo(accum);

            Span<int> psqt = GetPsqt(colorIdx);
            psqt.Clear();

            // 計算 mid-mirror 並列出活躍特徵
            bool midMirror = MidMirrorEncoder.RequiresMidMirror(board, perspective);
            int count = HalfKAv2Features.GetActiveFeatures(board, perspective, midMirror, features);

            sbyte[] ftW    = weights.FtWeights;
            int[]   ftPsqt = weights.FtPsqtWeights;

            // 累加各活躍特徵的 FT 權重
            for (int f = 0; f < count; f++)
            {
                int featIdx = features[f];
                int wOffset = featIdx * HalfDimensions;

                AddShortVector(accum, ftW, wOffset);

                int pOffset = featIdx * PsqtBuckets;
                for (int b = 0; b < PsqtBuckets; b++)
                    psqt[b] += ftPsqt[pOffset + b];
            }
        }
    }

    // ── 增量更新（Phase 4 完整實作）──────────────────────────────────

    /// <summary>
    /// 以增量方式更新指定視角的累加器。
    /// 在 Phase 4 前此方法不應被呼叫（透過 NeedsRefresh 旗標控制）。
    /// </summary>
    public void IncrementalUpdate(
        int colorIdx,
        ReadOnlySpan<int> added,
        ReadOnlySpan<int> removed,
        NnueWeights weights)
    {
        Span<short> accum = GetAccum(colorIdx);
        Span<int>   psqt  = GetPsqt(colorIdx);

        sbyte[] ftW    = weights.FtWeights;
        int[]   ftPsqt = weights.FtPsqtWeights;

        foreach (int featIdx in removed)
        {
            int wOffset = featIdx * HalfDimensions;
            SubShortVector(accum, ftW, wOffset);

            int pOffset = featIdx * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++)
                psqt[b] -= ftPsqt[pOffset + b];
        }

        foreach (int featIdx in added)
        {
            int wOffset = featIdx * HalfDimensions;
            AddShortVector(accum, ftW, wOffset);

            int pOffset = featIdx * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++)
                psqt[b] += ftPsqt[pOffset + b];
        }
    }

    // ── Transform（累加器 → FC0 輸入）───────────────────────────────

    /// <summary>
    /// 將雙側累加器轉換為 FC0 輸入（uint8[1024]）。
    /// output[j] = clamp(a0, 0,255) * clamp(a1, 0,255) / 512
    ///
    /// 快路徑：Vector256&lt;int&gt; 向量化成對乘法（AVX2 即可，16 對/週期）。
    /// </summary>
    public void Transform(int usPerspColorIdx, byte[] output)
    {
        for (int p = 0; p < 2; p++)
        {
            int colorIdx  = p == 0 ? usPerspColorIdx : 1 - usPerspColorIdx;
            int outOffset = p * (HalfDimensions / 2);  // 0 or 512

            Span<short> accum = GetAccum(colorIdx);

            TransformHalf(accum, output, outOffset);
        }
    }

    /// <summary>取得指定視角的 PSQT 累加值（用於最終分數計算）。</summary>
    public int GetPsqtValue(int usPerspColorIdx, int bucket)
    {
        int us   = usPerspColorIdx;
        int them = 1 - usPerspColorIdx;
        return (GetPsqt(us)[bucket] - GetPsqt(them)[bucket]) / 2;
    }

    // ── SIMD 輔助：short 向量加減法（累加 FT int8 權重到 int16 累加器）──

    /// <summary>
    /// accum[0..1023] += (short)ftW[wOffset..wOffset+1023]
    ///
    /// 快路徑：Avx512BW.ConvertToVector512Int16（VPMOVSXBW — 一條指令載入並符號擴展）
    ///         + VPADDW，32 shorts/週期（原實作：32 次純量 sign-extend）。
    /// 回退路徑：純量迴圈。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddShortVector(Span<short> accum, sbyte[] ftW, int wOffset)
    {
        Debug.Assert(accum.Length == HalfDimensions,
            $"AddShortVector: accum.Length={accum.Length} != HalfDimensions={HalfDimensions}");
        Debug.Assert(wOffset >= 0 && wOffset + HalfDimensions <= ftW.Length,
            $"AddShortVector: wOffset={wOffset} out of bounds (ftW.Length={ftW.Length})");

        ModifyShortVector(accum, ftW, wOffset, add: true);
    }

    /// <summary>
    /// accum[0..1023] -= (short)ftW[wOffset..wOffset+1023]
    /// 與 AddShortVector 鏡像，用於 IncrementalUpdate 的 removed 集合。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SubShortVector(Span<short> accum, sbyte[] ftW, int wOffset)
    {
        Debug.Assert(accum.Length == HalfDimensions,
            $"SubShortVector: accum.Length={accum.Length} != HalfDimensions={HalfDimensions}");
        Debug.Assert(wOffset >= 0 && wOffset + HalfDimensions <= ftW.Length,
            $"SubShortVector: wOffset={wOffset} out of bounds (ftW.Length={ftW.Length})");

        ModifyShortVector(accum, ftW, wOffset, add: false);
    }

    /// <summary>
    /// Add/Sub 共用實作。
    /// Avx512BW.ConvertToVector512Int16(Vector256&lt;sbyte&gt;) 對應 VPMOVSXBW zmm, ymm —
    /// 將 32 個 sbyte 一次符號擴展為 32 個 int16，無需逐元素純量轉換。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ModifyShortVector(Span<short> accum, sbyte[] ftW, int wOffset, bool add)
    {
        const int Stride = 32;  // Vector512<short>.Count

        if (Avx512BW.IsSupported)
        {
            ref short accumRef = ref MemoryMarshal.GetReference(accum);
            ref sbyte wRef     = ref ftW[wOffset];
            for (int n = 0; n <= HalfDimensions - Stride; n += Stride)
            {
                // 單條 VPMOVSXBW：載入 32 個 sbyte → 符號擴展至 512-bit int16
                var wVec = Avx512BW.ConvertToVector512Int16(
                    Vector256.LoadUnsafe(ref Unsafe.Add(ref wRef, n)));
                var aVec = Vector512.LoadUnsafe(ref Unsafe.Add(ref accumRef, n));
                var result = add ? Vector512.Add(aVec, wVec) : Vector512.Subtract(aVec, wVec);
                Vector512.StoreUnsafe(result, ref Unsafe.Add(ref accumRef, n));
            }
        }
        else if (add)
        {
            for (int n = 0; n < HalfDimensions; n++)
                accum[n] += ftW[wOffset + n];
        }
        else
        {
            for (int n = 0; n < HalfDimensions; n++)
                accum[n] -= ftW[wOffset + n];
        }
    }

    // ── SIMD 輔助：Transform 成對乘法 ──────────────────────────────

    /// <summary>
    /// 將累加器的一側（512 個 short）轉換為 FC0 輸入（512 個 byte）。
    ///
    /// output[j] = clamp(accum[j], 0,255) * clamp(accum[j+512], 0,255) / 512
    ///
    /// 快路徑（Avx2）：
    ///   1. Vector128.Max/Min clamp short → [0,255]
    ///   2. Avx2.ConvertToVector256Int32（VPMOVSXWD）widend short → int32
    ///   3. Vector256.Multiply × ShiftRightLogical 9
    ///   4. Vector256.Narrow 鏈（int32→ushort→byte）+ Vector64.StoreUnsafe — 一次寫 8 bytes
    ///      比原始 8 次 GetElement 提取快約 4-8×。
    /// 回退路徑：純量迴圈。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransformHalf(Span<short> accum, byte[] output, int outOffset)
    {
        const int Half = HalfDimensions / 2;  // 512

        if (Avx2.IsSupported)
        {
            const int Stride  = 8;  // Vector256<int>.Count
            var zero16 = Vector128<short>.Zero;
            var max16  = Vector128.Create((short)255);
            ref short aRef   = ref MemoryMarshal.GetReference(accum);
            ref byte  outRef = ref output[outOffset];

            for (int j = 0; j <= Half - Stride; j += Stride)
            {
                // 載入兩側各 8 個 short，SIMD clamp 至 [0, 255]
                var a0 = Vector128.Min(Vector128.Max(
                    Vector128.LoadUnsafe(ref Unsafe.Add(ref aRef, j)), zero16), max16);
                var a1 = Vector128.Min(Vector128.Max(
                    Vector128.LoadUnsafe(ref Unsafe.Add(ref aRef, Half + j)), zero16), max16);

                // VPMOVSXWD：符號擴展 8 個 int16 → 8 個 int32（clamp 後均非負，等同零擴展）
                var p0 = Avx2.ConvertToVector256Int32(a0);
                var p1 = Avx2.ConvertToVector256Int32(a1);

                // 成對乘法（0..65025），右移 9（÷512）→ 結果 0..127
                var product = Vector256.ShiftRightLogical(Vector256.Multiply(p0, p1), 9);

                // 縮窄 int32 → ushort → byte（截斷下 8/16 位；值 ≤ 127，截斷安全）
                var as16 = Vector256.Narrow(product.AsUInt32(), Vector256<uint>.Zero);
                var as8  = Vector256.Narrow(as16, Vector256<ushort>.Zero);

                // 一次寫入 8 bytes（Vector64 store = 64-bit MOV）
                Vector64.StoreUnsafe(as8.GetLower().GetLower(), ref Unsafe.Add(ref outRef, j));
            }
        }
        else
        {
            for (int j = 0; j < Half; j++)
            {
                int a0 = Math.Clamp((int)accum[j],        0, 255);
                int a1 = Math.Clamp((int)accum[j + Half], 0, 255);
                output[outOffset + j] = (byte)((uint)(a0 * a1) / 512);
            }
        }
    }
}
