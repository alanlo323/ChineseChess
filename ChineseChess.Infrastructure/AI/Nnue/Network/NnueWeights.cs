namespace ChineseChess.Infrastructure.AI.Nnue.Network;

/// <summary>
/// 從 .nnue 檔載入的完整網路權重（只讀，執行期共用）。
/// 僅包含 HalfKAv2_hm 特徵部分（不含 FullThreats）。
/// </summary>
public sealed class NnueWeights
{
    // ─── Feature Transformer ──────────────────────────────────────────
    /// <summary>FT 偏置，int16[HalfDimensions=1024]，乘以 2 縮放儲存。</summary>
    public short[] FtBiases { get; init; } = [];

    /// <summary>FT 主權重，int8[Dimensions * HalfDimensions = 16536 * 1024]，
    /// 佈局：weights[featureIdx * 1024 + neuronIdx]。</summary>
    public sbyte[] FtWeights { get; init; } = [];

    /// <summary>FT PSQT 權重，int32[Dimensions * PsqtBuckets = 16536 * 16]，
    /// 佈局：psqtWeights[featureIdx * 16 + bucketIdx]。</summary>
    public int[] FtPsqtWeights { get; init; } = [];

    // ─── 16 個 LayerStack（依剩餘大子數選桶）──────────────────────────
    public NetworkStack[] Stacks { get; init; } = [];

    /// <summary>模型描述字串（來自檔頭）。</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>單一 LayerStack 的全連結層權重。</summary>
public sealed class NetworkStack
{
    /// <summary>FC0 偏置，int32[L2+1=16]。</summary>
    public int[] Fc0Biases { get; init; } = [];

    /// <summary>FC0 權重，int8[(L2+1) * PaddedL1 = 16 * 1024]，
    /// 佈局：weights[inputIdx + outputIdx * 1024]。</summary>
    public sbyte[] Fc0Weights { get; init; } = [];

    /// <summary>FC1 偏置，int32[L3=32]。</summary>
    public int[] Fc1Biases { get; init; } = [];

    /// <summary>FC1 權重，int8[L3 * Padded(L2*2) = 32 * 32]，
    /// 佈局：weights[inputIdx + outputIdx * 32]。</summary>
    public sbyte[] Fc1Weights { get; init; } = [];

    /// <summary>FC2 偏置，int32[1]。</summary>
    public int[] Fc2Biases { get; init; } = [];

    /// <summary>FC2 權重，int8[Padded(L3) = 32]。</summary>
    public sbyte[] Fc2Weights { get; init; } = [];
}
