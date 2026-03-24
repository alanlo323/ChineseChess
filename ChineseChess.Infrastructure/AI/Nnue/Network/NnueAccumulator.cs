using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Nnue.Features;

namespace ChineseChess.Infrastructure.AI.Nnue.Network;

/// <summary>
/// 雙視角 HalfKAv2 特徵轉換器累加器。
///
/// 每個視角維護一個 int16[1024] 累加向量（初始化為 FT 偏置），
/// 當棋子移動時透過增量更新（Phase 4）避免全量重算。
///
/// 「堆疊」設計：搜尋推進時 Push，回退時 Pop，確保搜尋樹各節點
/// 累加器狀態一致（搜尋深度上限 = MaxDepth）。
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

            // 累加各活躍特徵的 FT 權重（int8 sign-extend 至 int16）
            sbyte[] ftW    = weights.FtWeights;
            int[]   ftPsqt = weights.FtPsqtWeights;

            for (int f = 0; f < count; f++)
            {
                int featIdx = features[f];
                int wOffset = featIdx * HalfDimensions;
                for (int n = 0; n < HalfDimensions; n++)
                    accum[n] += ftW[wOffset + n];

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
        // 從前一深度複製（Push 已複製，此處為增量修改當前層）
        Span<short> accum = GetAccum(colorIdx);
        Span<int>   psqt  = GetPsqt(colorIdx);

        sbyte[] ftW    = weights.FtWeights;
        int[]   ftPsqt = weights.FtPsqtWeights;

        foreach (int featIdx in removed)
        {
            int wOffset = featIdx * HalfDimensions;
            for (int n = 0; n < HalfDimensions; n++)
                accum[n] -= ftW[wOffset + n];

            int pOffset = featIdx * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++)
                psqt[b] -= ftPsqt[pOffset + b];
        }

        foreach (int featIdx in added)
        {
            int wOffset = featIdx * HalfDimensions;
            for (int n = 0; n < HalfDimensions; n++)
                accum[n] += ftW[wOffset + n];

            int pOffset = featIdx * PsqtBuckets;
            for (int b = 0; b < PsqtBuckets; b++)
                psqt[b] += ftPsqt[pOffset + b];
        }
    }

    // ── Transform（累加器 → FC0 輸入）───────────────────────────────

    /// <summary>
    /// 將雙側累加器轉換為 FC0 輸入（uint8[1024]）。
    /// 純量實作：output[j] = clamp(a0+ta0, 0,255) * clamp(a1+ta1, 0,255) / 512
    /// 其中 ta = 0（不含 FullThreats）。
    /// </summary>
    public void Transform(int usPerspColorIdx, byte[] output)
    {
        for (int p = 0; p < 2; p++)
        {
            int colorIdx = p == 0 ? usPerspColorIdx : 1 - usPerspColorIdx;
            int outOffset = p * (HalfDimensions / 2);  // 0 or 512

            Span<short> accum = GetAccum(colorIdx);

            for (int j = 0; j < HalfDimensions / 2; j++)
            {
                int a0 = Math.Clamp((int)accum[j],                       0, 255);
                int a1 = Math.Clamp((int)accum[j + HalfDimensions / 2],  0, 255);
                output[outOffset + j] = (byte)((uint)(a0 * a1) / 512);
            }
        }
    }

    /// <summary>取得指定視角的 PSQT 累加值（用於最終分數計算）。</summary>
    public int GetPsqtValue(int usPerspColorIdx, int bucket)
    {
        int us   = usPerspColorIdx;
        int them = 1 - usPerspColorIdx;
        return (GetPsqt(us)[bucket] - GetPsqt(them)[bucket]) / 2;
    }
}
