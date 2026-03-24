using System.Text;

namespace ChineseChess.Infrastructure.AI.Nnue.Network;

/// <summary>
/// Pikafish .nnue 二進位格式的讀取工具。
///
/// 檔案結構（小端序）：
///   Header : uint32 version | uint32 hash | uint32 descLen | byte[descLen] desc
///   FT     : uint32 FtHash | LEB128 int16[1024] biases | int8[45649*1024] threatWeights
///            | int8[16536*1024] weights | LEB128 int32[(45649+16536)*16] combinedPsqt
///   Stacks : 16 × (uint32 stackHash | FC0 | FC1 | FC2)
///     FC0 = int32[16] biases + int8[16*1024] weights (plain little-endian)
///     FC1 = int32[32] biases + int8[32*32]   weights
///     FC2 = int32[1]  biases + int8[1*32]    weights
/// </summary>
public static class NnueFileFormat
{
    // ── Pikafish 格式常數 ─────────────────────────────────────────────
    public const uint   FileVersion       = 0x7AF32F20u;
    public const int    HalfDimensions    = 1024;        // FT 輸出維度（每側）
    public const int    Dimensions        = 16_536;      // HalfKAv2 特徵總數
    public const int    ThreatDimensions  = 45_649;      // FullThreats 特徵總數
    public const int    PsqtBuckets       = 16;          // PSQT bucket 數
    public const int    LayerStacksNb     = 16;          // LayerStack 數
    public const int    L2                = 15;          // FC0 輸出（不含 PSQT 神經元）
    public const int    L3                = 32;          // FC1 輸出
    private const int   Leb128MagicLen    = 17;          // "COMPRESSED_LEB128"
    private static readonly byte[] Leb128Magic =
        Encoding.ASCII.GetBytes("COMPRESSED_LEB128");

    // ── 公開 API ─────────────────────────────────────────────────────

    /// <summary>從 .nnue 檔路徑讀取並回傳 NnueWeights。</summary>
    /// <exception cref="InvalidDataException">格式不符或版本不匹配。</exception>
    public static NnueWeights LoadWeights(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1 << 20);
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        return Read(br, filePath);
    }

    // ── 私有讀取流程 ─────────────────────────────────────────────────

    private static NnueWeights Read(BinaryReader br, string filePath)
    {
        // 1. 檔案頭
        uint version = br.ReadUInt32();
        if (version != FileVersion)
            throw new InvalidDataException(
                $"NNUE 版本不符：預期 0x{FileVersion:X}，實際 0x{version:X}（{filePath}）");

        br.ReadUInt32();  // 整體 hash（略過驗證）
        uint descLen = br.ReadUInt32();
        if (descLen > 4096)
            throw new InvalidDataException($"NNUE 描述欄位過長（{descLen} 位元組），可能為損壞檔案");
        string description = descLen > 0
            ? Encoding.UTF8.GetString(br.ReadBytes((int)descLen))
            : string.Empty;

        // 2. Feature Transformer
        br.ReadUInt32();  // FT hash（略過驗證）

        short[] ftBiases     = ReadLeb128Int16(br, HalfDimensions);
        SkipBytes(br, (long)ThreatDimensions * HalfDimensions);  // threatWeights（不使用）
        sbyte[] ftWeights    = ReadSbyteArray(br, (long)Dimensions * HalfDimensions);

        // 合併 PSQT：前 ThreatDimensions*16 為 threat（略過），後 Dimensions*16 為 HalfKAv2
        int[] combinedPsqt = ReadLeb128Int32(br, (ThreatDimensions + Dimensions) * PsqtBuckets);
        int[] ftPsqtWeights = combinedPsqt[(ThreatDimensions * PsqtBuckets)..];

        // 3. 16 個 LayerStack
        var stacks = new NetworkStack[LayerStacksNb];
        for (int s = 0; s < LayerStacksNb; s++)
        {
            br.ReadUInt32();  // stack hash（略過驗證）
            stacks[s] = ReadNetworkStack(br);
        }

        return new NnueWeights
        {
            Description  = description,
            FtBiases     = ftBiases,
            FtWeights    = ftWeights,
            FtPsqtWeights = ftPsqtWeights,
            Stacks       = stacks,
        };
    }

    private static NetworkStack ReadNetworkStack(BinaryReader br)
    {
        // FC0 : AffineTransformSparseInput<1024, L2+1=16>
        int[]   fc0Biases  = ReadInt32Array(br, L2 + 1);
        sbyte[] fc0Weights = ReadSbyteArray(br, (long)(L2 + 1) * HalfDimensions);

        // FC1 : AffineTransform<L2*2=30, L3=32>  (padded input = 32)
        int   fc1PaddedIn = CeilToMultiple(L2 * 2, 32);  // = 32
        int[]   fc1Biases  = ReadInt32Array(br, L3);
        sbyte[] fc1Weights = ReadSbyteArray(br, (long)L3 * fc1PaddedIn);

        // FC2 : AffineTransform<L3=32, 1>
        int[]   fc2Biases  = ReadInt32Array(br, 1);
        sbyte[] fc2Weights = ReadSbyteArray(br, (long)1 * CeilToMultiple(L3, 32));

        return new NetworkStack
        {
            Fc0Biases  = fc0Biases,
            Fc0Weights = fc0Weights,
            Fc1Biases  = fc1Biases,
            Fc1Weights = fc1Weights,
            Fc2Biases  = fc2Biases,
            Fc2Weights = fc2Weights,
        };
    }

    // ── 原始型別讀取 ─────────────────────────────────────────────────

    private static int[] ReadInt32Array(BinaryReader br, int count)
    {
        var arr = new int[count];
        for (int i = 0; i < count; i++)
            arr[i] = br.ReadInt32();
        return arr;
    }

    private static sbyte[] ReadSbyteArray(BinaryReader br, long count)
    {
        var bytes = new byte[count];
        br.ReadExactly(bytes);
        return Array.ConvertAll(bytes, b => (sbyte)b);
    }

    private static void SkipBytes(BinaryReader br, long count)
    {
        // 分塊跳過，避免一次分配過大 buffer
        const int Chunk = 1 << 20;  // 1 MB
        long remaining = count;
        var  buf = new byte[Chunk];
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, Chunk);
            br.ReadExactly(buf.AsSpan(0, toRead));
            remaining -= toRead;
        }
    }

    // ── LEB128 解碼 ──────────────────────────────────────────────────

    private static short[] ReadLeb128Int16(BinaryReader br, int count)
    {
        var compressed = ReadLeb128Compressed(br);
        var result = new short[count];
        int pos = 0;
        for (int i = 0; i < count; i++)
        {
            int value = 0, shift = 0;
            byte b;
            do
            {
                if (pos >= compressed.Length)
                    throw new InvalidDataException($"LEB128 int16 資料過短（已讀 {i}/{count} 個值）");
                b = compressed[pos++];
                value |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);

            // 符號延伸
            if (shift < 16 && (b & 0x40) != 0)
                value |= ~((1 << shift) - 1);
            result[i] = (short)value;
        }
        return result;
    }

    private static int[] ReadLeb128Int32(BinaryReader br, int count)
    {
        var compressed = ReadLeb128Compressed(br);
        var result = new int[count];
        int pos = 0;
        for (int i = 0; i < count; i++)
        {
            int value = 0, shift = 0;
            byte b;
            do
            {
                if (pos >= compressed.Length)
                    throw new InvalidDataException($"LEB128 int32 資料過短（已讀 {i}/{count} 個值）");
                b = compressed[pos++];
                value |= (b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);

            if (shift < 32 && (b & 0x40) != 0)
                value |= ~((1 << shift) - 1);
            result[i] = value;
        }
        return result;
    }

    /// <summary>讀取 LEB128 魔術字串 + uint32 大小 + 壓縮資料。</summary>
    private static byte[] ReadLeb128Compressed(BinaryReader br)
    {
        byte[] magic = br.ReadBytes(Leb128MagicLen);
        for (int i = 0; i < Leb128MagicLen; i++)
            if (magic[i] != Leb128Magic[i])
                throw new InvalidDataException("LEB128 魔術字串不符");

        uint bytesLeft = br.ReadUInt32();
        // 上限 64 MB，防止惡意或損壞的檔案觸發記憶體分配 DoS
        if (bytesLeft > 64 * 1024 * 1024)
            throw new InvalidDataException($"LEB128 壓縮區塊過大（{bytesLeft} 位元組），可能為損壞檔案");

        return br.ReadBytes((int)bytesLeft);
    }

    private static int CeilToMultiple(int n, int b) => (n + b - 1) / b * b;
}
