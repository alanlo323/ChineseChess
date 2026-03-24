using System.Text;
using ChineseChess.Infrastructure.AI.Nnue.Network;

namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// 將 float32 訓練網路量化並寫出為 Pikafish 相容的 .nnue 格式。
///
/// 量化對映：
///   FT biases  : float × 2      → int16（×2 縮放）
///   FT weights : clamp(-127,127) → int8
///   FT PSQT    : float × 64     → int32（×WeightScaleBits 縮放）
///   FC biases  : float × 64     → int32
///   FC weights : clamp(-127,127) → int8
/// </summary>
public static class NnueModelExporter
{
    private const int L1          = NnueFileFormat.HalfDimensions;
    private const int L2          = NnueFileFormat.L2;
    private const int L3          = NnueFileFormat.L3;
    private const int PsqtBuckets = NnueFileFormat.PsqtBuckets;
    private const int Dims        = NnueFileFormat.Dimensions;
    private const int Stacks      = NnueFileFormat.LayerStacksNb;
    private const float WeightScale = 64f;  // 1 << 6

    /// <summary>
    /// 將訓練網路量化並匯出至指定路徑。
    /// </summary>
    /// <param name="network">已訓練的 float32 網路。</param>
    /// <param name="outputPath">輸出 .nnue 檔路徑。</param>
    /// <param name="description">寫入檔頭的模型描述字串。</param>
    public static void Export(TrainingNetwork network, string outputPath, string description = "")
    {
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 1 << 20);
        using var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        // ── 1. 檔頭 ──────────────────────────────────────────────────
        bw.Write(NnueFileFormat.FileVersion);
        bw.Write(0u);  // hash（略）
        var descBytes = Encoding.UTF8.GetBytes(description);
        bw.Write((uint)descBytes.Length);
        bw.Write(descBytes);

        // ── 2. Feature Transformer ───────────────────────────────────
        bw.Write(0u);  // FT hash（略）

        // FT biases：float × 2 → int16（LEB128 壓縮）
        var ftBiasesQ = new short[L1];
        for (int i = 0; i < L1; i++)
            ftBiasesQ[i] = (short)Math.Clamp((int)(network.FtBiases[i] * 2f), short.MinValue, short.MaxValue);
        WriteLeb128Int16(bw, ftBiasesQ);

        // threatWeights：全零（ThreatDimensions × L1 bytes）
        WriteThreatWeightsPlaceholder(bw);

        // FT weights：float clamp → int8
        var ftWeightsQ = new sbyte[(long)Dims * L1];
        for (long i = 0; i < ftWeightsQ.LongLength; i++)
            ftWeightsQ[i] = (sbyte)Math.Clamp((int)Math.Round(network.FtWeights[i]), -127, 127);
        WriteRawSbytes(bw, ftWeightsQ);

        // PSQT（合併 ThreatDimensions×16 全零 + Dims×16）
        var psqtQ = new int[(NnueFileFormat.ThreatDimensions + Dims) * PsqtBuckets];
        for (long i = 0; i < (long)Dims * PsqtBuckets; i++)
            psqtQ[(long)NnueFileFormat.ThreatDimensions * PsqtBuckets + i] =
                (int)Math.Round(network.FtPsqt[i] * WeightScale);
        WriteLeb128Int32(bw, psqtQ);

        // ── 3. 16 個 LayerStack ──────────────────────────────────────
        for (int s = 0; s < Stacks; s++)
        {
            bw.Write(0u);  // stack hash（略）

            // FC0 biases：float × 64 → int32
            for (int i = 0; i <= L2; i++)
                bw.Write((int)Math.Round(network.Fc0Biases[s][i] * WeightScale));

            // FC0 weights：float → int8
            var fc0w = new sbyte[(L2 + 1) * L1];
            for (int i = 0; i < fc0w.Length; i++)
                fc0w[i] = (sbyte)Math.Clamp((int)Math.Round(network.Fc0Weights[s][i]), -127, 127);
            WriteRawSbytes(bw, fc0w);

            // FC1 biases：float × 64 → int32
            for (int i = 0; i < L3; i++)
                bw.Write((int)Math.Round(network.Fc1Biases[s][i] * WeightScale));

            // FC1 weights：float → int8（padded input = 32）
            var fc1w = new sbyte[L3 * 32];
            for (int i = 0; i < fc1w.Length; i++)
                fc1w[i] = (sbyte)Math.Clamp((int)Math.Round(network.Fc1Weights[s][i]), -127, 127);
            WriteRawSbytes(bw, fc1w);

            // FC2 bias：float × 64 → int32
            bw.Write((int)Math.Round(network.Fc2Biases[s][0] * WeightScale));

            // FC2 weights：float → int8（padded input = 32）
            var fc2w = new sbyte[32];
            for (int i = 0; i < 32; i++)
                fc2w[i] = (sbyte)Math.Clamp((int)Math.Round(network.Fc2Weights[s][i]), -127, 127);
            WriteRawSbytes(bw, fc2w);
        }
    }

    // ── 私有寫出工具 ─────────────────────────────────────────────────

    private static void WriteThreatWeightsPlaceholder(BinaryWriter bw)
    {
        // ThreatDimensions × L1 全零 int8（Pikafish 忽略此段）
        const int ThreatDims = NnueFileFormat.ThreatDimensions;
        const int blockSize = 1 << 20;
        long remaining = (long)ThreatDims * L1;
        var buf = new byte[blockSize];
        while (remaining > 0)
        {
            int toWrite = (int)Math.Min(remaining, blockSize);
            bw.Write(buf, 0, toWrite);
            remaining -= toWrite;
        }
    }

    private static void WriteRawSbytes(BinaryWriter bw, sbyte[] data)
    {
        // 直接轉換 sbyte[] → byte[] 並寫出
        var bytes = new byte[data.Length];
        Buffer.BlockCopy(data, 0, bytes, 0, data.Length);
        bw.Write(bytes);
    }

    /// <summary>寫出 LEB128 壓縮的 int16 陣列（含 "COMPRESSED_LEB128" magic + uint32 大小）。</summary>
    private static void WriteLeb128Int16(BinaryWriter bw, short[] data)
    {
        var compressed = EncodeLeb128Int16(data);
        bw.Write(Encoding.ASCII.GetBytes("COMPRESSED_LEB128"));
        bw.Write((uint)compressed.Length);
        bw.Write(compressed);
    }

    /// <summary>寫出 LEB128 壓縮的 int32 陣列（含 magic + uint32 大小）。</summary>
    private static void WriteLeb128Int32(BinaryWriter bw, int[] data)
    {
        var compressed = EncodeLeb128Int32(data);
        bw.Write(Encoding.ASCII.GetBytes("COMPRESSED_LEB128"));
        bw.Write((uint)compressed.Length);
        bw.Write(compressed);
    }

    private static byte[] EncodeLeb128Int16(short[] data)
    {
        var buf = new List<byte>(data.Length * 2);
        foreach (int v in data)
        {
            int value = v;
            while (true)
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                bool signBit = (b & 0x40) != 0;
                if ((value == 0 && !signBit) || (value == -1 && signBit))
                {
                    buf.Add(b);
                    break;
                }
                buf.Add((byte)(b | 0x80));
            }
        }
        return [.. buf];
    }

    private static byte[] EncodeLeb128Int32(int[] data)
    {
        var buf = new List<byte>(data.Length * 4);
        foreach (int v in data)
        {
            int value = v;
            while (true)
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                bool signBit = (b & 0x40) != 0;
                if ((value == 0 && !signBit) || (value == -1 && signBit))
                {
                    buf.Add(b);
                    break;
                }
                buf.Add((byte)(b | 0x80));
            }
        }
        return [.. buf];
    }
}
