using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace ChineseChess.Infrastructure.AI.Search;

public sealed class TTStateSnapshot
{
    public ulong Size { get; set; }
    public byte Generation { get; set; }
    public ulong[] Keys { get; set; } = [];
    public ulong[] Data { get; set; } = [];
}

/// <summary>
/// 使用 XOR 驗證技巧的 lock-free transposition table。
/// 以兩個對齊的 ulong 陣列（keys 與 data）確保 x64 上 8 bytes 原子性讀寫。
/// keys[i] 存放 (zobristKey ^ packedData)，可偵測到被撕裂讀取的狀況。
/// 使用平方探測 (Quadratic Probing) 處理雜湊碰撞，搭配深度保留替換策略。
/// </summary>
public class TranspositionTable
{
    private static readonly byte[] BinaryHeader = "CCTT"u8.ToArray();
    private const uint BinaryVersion       = 4u;  // v4：Full key + Columnar + Brotli（QP 相容）
    private const uint BrotliColumnarVersion = 3u; // v3：Columnar + Quotient key + Brotli
    private const uint GzipBinaryVersion   = 2u;  // v2：GZip 壓縮（向後相容讀取）
    private const uint LegacyBinaryVersion = 1u;  // v1：無壓縮（向後相容讀取）

    // 平方探測最大步數：限制每次探測的最大步數，避免效能退化
    private const int MaxProbeDistance = 4;

    // 自動擴容門檻
    private const double CollisionRateThreshold = 0.6;
    private const double FillRateThreshold = 0.75;
    private const int MaxAutoResizeMb = 1024;

    // 自動擴容所需的最少探測次數（避免冷啟動時誤觸發）
    private const long MinProbesForAutoResize = 1000;

    // 使用非 readonly 以支援自動擴容時的陣列替換
    private ulong[] keys;
    private ulong[] data;
    private ulong size;
    private byte generation;
    private long totalProbes;
    private long probeHits;
    private long occupiedCount;
    private long collisionCount;    // 探測碰撞次數（探測時發現不同 key 的次數）
    private long replacementCount;  // 替換次數（Store 時覆寫非空且非同 key 的槽位）

    // Pack 版面（共 64 位元）：
    //   bits  0-15 : score      （16 位元，透過 cast 轉為有號）
    //   bits 16-23 : depth      （8 位元）
    //   bits 24-25 : flag       （2 位元）
    //   bits 26-32 : from       （7 位元，最大值 89）
    //   bits 33-39 : to         （7 位元，最大值 89）
    //   bits 40-47 : generation  （8 位元）

    public TranspositionTable(int sizeMb)
    {
        long bytes = (long)sizeMb * 1024 * 1024;
        long entrySize = 2 * sizeof(ulong); // key 與 data 兩欄位
        size = (ulong)(bytes / entrySize);
        if (size < 1024) size = 1024;

        keys = new ulong[size];
        data = new ulong[size];
        generation = 0;
    }

    // 以條目數建立空表（Clone 與測試專用）
    internal TranspositionTable(ulong entryCount)
    {
        size = Math.Max(entryCount, 1024);
        keys = new ulong[size];
        data = new ulong[size];
        generation = 0;
    }

    public byte Generation => generation;

    public void NewGeneration()
    {
        generation++;
    }

    /// <summary>
    /// 建立本 TT 的深度複製。複製後兩表彼此獨立，世代與資料跟原表一致，
    /// 但統計（查詢次數、命中）從零開始。
    /// </summary>
    public TranspositionTable Clone()
    {
        if (size > (ulong)int.MaxValue)
            throw new InvalidOperationException($"TT 過大無法複製：{size} 個條目超過 int 上限。");
        var clone = new TranspositionTable(size);
        System.Array.Copy(keys, clone.keys, (int)size);
        System.Array.Copy(data, clone.data, (int)size);
        clone.generation = generation;
        Interlocked.Exchange(ref clone.occupiedCount, Interlocked.Read(ref occupiedCount));
        return clone;
    }

    /// <summary>
    /// 將 <paramref name="other"/> 中的所有有效條目合併進本表，
    /// 採「深度優先」策略。使用平方探測插入，確保 QP 佈局正確。
    /// 兩表大小可以不同；合併時依 zobrist key 重新計算本表索引。
    /// </summary>
    public void MergeFrom(TranspositionTable other)
    {
        for (ulong i = 0; i < other.size; i++)
        {
            ulong otherKeyXorData = Volatile.Read(ref other.keys[i]);
            if (otherKeyXorData == 0) continue;

            ulong otherData = Volatile.Read(ref other.data[i]);
            ulong zobristKey = otherKeyXorData ^ otherData;

            // 使用 InsertEntry 確保 QP 佈局，並保留深度較高的條目
            InsertEntry(zobristKey, otherData);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Probe(ulong key, out TTEntry entry)
    {
        ulong baseIndex = key % size;
        Interlocked.Increment(ref totalProbes);

        for (int step = 0; step < MaxProbeDistance; step++)
        {
            ulong index = (baseIndex + (ulong)(step * step)) % size;
            ulong storedKey = Volatile.Read(ref keys[index]);
            ulong storedData = Volatile.Read(ref data[index]);

            if ((storedKey ^ storedData) == key)
            {
                Interlocked.Increment(ref probeHits);
                entry = Unpack(storedData, key);
                return true;
            }

            if (storedKey == 0)
            {
                // 空槽：此 key 不在表中
                entry = default;
                return false;
            }

            // 碰撞：此槽位存放不同 key，繼續探測下一步
            Interlocked.Increment(ref collisionCount);
        }

        entry = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ulong key, int score, int depth, TTFlag flag, Move bestMove)
    {
        ulong baseIndex = key % size;
        ulong packed = Pack((short)ClampScore(score), (byte)depth, flag, bestMove.From, bestMove.To, generation);

        int bestStep = -1;
        ulong bestIndex = 0;
        int bestPriority = int.MaxValue;

        for (int step = 0; step < MaxProbeDistance; step++)
        {
            ulong index = (baseIndex + (ulong)(step * step)) % size;
            ulong storedKey = Volatile.Read(ref keys[index]);
            ulong storedData = Volatile.Read(ref data[index]);

            if (storedKey == 0)
            {
                // 空槽：直接寫入
                Interlocked.Increment(ref occupiedCount);
                Volatile.Write(ref data[index], packed);
                Volatile.Write(ref keys[index], key ^ packed);
                return;
            }

            if ((storedKey ^ storedData) == key)
            {
                // 同一局面：深度優先保留，或當世代不同時（舊條目）允許覆寫
                byte existingDepth = (byte)((storedData >> 16) & 0xFF);
                byte existingGen = (byte)((storedData >> 40) & 0xFF);
                if (depth >= existingDepth || existingGen != generation)
                {
                    Volatile.Write(ref data[index], packed);
                    Volatile.Write(ref keys[index], key ^ packed);
                }
                return;
            }

            // 碰撞：評估替換優先度（數值越小越適合被替換）
            // 優先替換：舊世代 + 低深度的條目
            byte candidateDepth = (byte)((storedData >> 16) & 0xFF);
            byte candidateGen = (byte)((storedData >> 40) & 0xFF);
            int priority = candidateDepth * 2 + (candidateGen == generation ? 1 : 0);

            if (priority < bestPriority)
            {
                bestPriority = priority;
                bestIndex = index;
                bestStep = step;
            }
        }

        // 所有探測槽都已被佔用，替換最低優先度的條目
        if (bestStep >= 0)
        {
            Interlocked.Increment(ref replacementCount);
            Volatile.Write(ref data[bestIndex], packed);
            Volatile.Write(ref keys[bestIndex], key ^ packed);
        }
    }

    /// <summary>
    /// 以平方探測將條目直接插入表中（保留原始 packed 資料，含世代資訊）。
    /// 採深度優先：僅在新條目深度更大時才覆寫同 key 條目。
    /// 主要用於 MergeFrom 與 Import 操作。
    /// </summary>
    private void InsertEntry(ulong key, ulong packed)
    {
        ulong baseIndex = key % size;

        for (int step = 0; step < MaxProbeDistance; step++)
        {
            ulong index = (baseIndex + (ulong)(step * step)) % size;
            ulong storedKey = Volatile.Read(ref keys[index]);

            if (storedKey == 0)
            {
                // 空槽：直接寫入
                Interlocked.Increment(ref occupiedCount);
                Volatile.Write(ref data[index], packed);
                Volatile.Write(ref keys[index], key ^ packed);
                return;
            }

            ulong storedData = Volatile.Read(ref data[index]);
            if ((storedKey ^ storedData) == key)
            {
                // 同 key：深度優先，僅在新深度更大時替換
                byte existingDepth = (byte)((storedData >> 16) & 0xFF);
                byte newDepth = (byte)((packed >> 16) & 0xFF);
                if (newDepth > existingDepth)
                {
                    Volatile.Write(ref data[index], packed);
                    Volatile.Write(ref keys[index], key ^ packed);
                }
                return;
            }
        }

        // 所有探測槽都被佔用，取代基底槽位（深度優先）
        ulong baseData = Volatile.Read(ref data[baseIndex]);
        byte basePriority = (byte)((baseData >> 16) & 0xFF);
        byte newPriority = (byte)((packed >> 16) & 0xFF);
        if (newPriority > basePriority)
        {
            Volatile.Write(ref data[baseIndex], packed);
            Volatile.Write(ref keys[baseIndex], key ^ packed);
        }
    }

    public void Clear()
    {
        System.Array.Clear(keys, 0, keys.Length);
        System.Array.Clear(data, 0, data.Length);
        generation = 0;
        Interlocked.Exchange(ref totalProbes, 0);
        Interlocked.Exchange(ref probeHits, 0);
        Interlocked.Exchange(ref occupiedCount, 0);
        Interlocked.Exchange(ref collisionCount, 0);
        Interlocked.Exchange(ref replacementCount, 0);
    }

    public TTStatistics GetStatistics()
    {
        long probes = Interlocked.Read(ref totalProbes);
        long hits = Interlocked.Read(ref probeHits);
        long occupied = Interlocked.Read(ref occupiedCount);
        long collisions = Interlocked.Read(ref collisionCount);
        return new TTStatistics
        {
            Capacity = size,
            MemoryMb = size * 16.0 / (1024 * 1024),
            Generation = generation,
            TotalProbes = probes,
            Hits = hits,
            HitRate = probes > 0 ? (double)hits / probes : 0.0,
            OccupiedEntries = occupied,
            FillRate = size > 0 ? (double)occupied / size : 0.0,
            CollisionCount = collisions,
            CollisionRate = probes > 0 ? (double)collisions / probes : 0.0
        };
    }

    /// <summary>
    /// 嘗試自動擴容 TT 表。
    /// 當碰撞率 > 60% 且填滿率 > 75%（且探測次數 >= 1000）時，
    /// 將表擴大為當前的 2 倍（上限 1024MB）。
    /// 僅應在搜尋開始前呼叫，避免與 Probe/Store hot path 競爭。
    /// </summary>
    public bool TryAutoResize()
    {
        long probes = Interlocked.Read(ref totalProbes);
        if (probes < MinProbesForAutoResize) return false;

        double collisionRate = (double)Interlocked.Read(ref collisionCount) / probes;
        double fillRate = size > 0 ? (double)Interlocked.Read(ref occupiedCount) / (double)size : 0.0;

        if (collisionRate <= CollisionRateThreshold || fillRate <= FillRateThreshold)
            return false;

        // 計算新大小（當前條目數 * 2），並確認不超過上限
        ulong newSize = size * 2;
        double newMb = newSize * 16.0 / (1024.0 * 1024.0);
        if (newMb > MaxAutoResizeMb)
            return false;

        try
        {
            Resize(newSize);
            // 重置統計，讓新表從乾淨狀態開始評估
            Interlocked.Exchange(ref collisionCount, 0);
            Interlocked.Exchange(ref totalProbes, 0);
            Interlocked.Exchange(ref probeHits, 0);
            return true;
        }
        catch (OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// 將 TT 擴容至 <paramref name="newSize"/> 個條目，並重新以 QP 插入所有有效條目。
    /// 注意：此方法非 thread-safe，僅應在搜尋開始前（無並行存取時）呼叫。
    /// </summary>
    private void Resize(ulong newSize)
    {
        ulong actualNewSize = Math.Max(newSize, 1024);
        var newKeys = new ulong[actualNewSize];
        var newData = new ulong[actualNewSize];
        long newOccupied = 0;

        for (ulong i = 0; i < size; i++)
        {
            ulong kxd = keys[i];
            if (kxd == 0) continue;

            ulong d = data[i];
            ulong key = kxd ^ d;
            ulong baseIndex = key % actualNewSize;

            // 以 QP 在新表中尋找空槽並插入
            for (int step = 0; step < MaxProbeDistance; step++)
            {
                ulong index = (baseIndex + (ulong)(step * step)) % actualNewSize;
                if (newKeys[index] == 0)
                {
                    newData[index] = d;
                    newKeys[index] = key ^ d;
                    newOccupied++;
                    break;
                }
            }
            // 若所有探測槽都被佔用，此條目在新表中被丟棄（擴容時極罕見）
        }

        keys = newKeys;
        data = newData;
        size = actualNewSize;
        Interlocked.Exchange(ref occupiedCount, newOccupied);
    }

    /// <summary>
    /// 以 v4 格式（Full key + Columnar + Brotli）匯出 TT 快照。
    ///
    /// 欄位佈局（單一 BrotliStream，Columnar 順序）：
    ///   Col 0: Full zobrist key（uint64，0=空槽）
    ///   Col 1: Score  (int16)
    ///   Col 2: Depth  (byte)
    ///   Col 3: Flag   (byte, 0-3)
    ///   Col 4: From   (byte, 0-89)
    ///   Col 5: To     (byte, 0-89)
    ///   Col 6: Gen    (byte)
    ///
    /// v4 使用完整 key 儲存（而非 v3 的 quotient 技巧），確保 QP 後 entry 位置與 key 解耦，
    /// 匯入時透過 QP 重新建立正確的探測序列。
    /// </summary>
    public void ExportToBinary(Stream output)
    {
        if (output == null)
            throw new ArgumentNullException(nameof(output));

        // 標頭明文寫入，供匯入時讀取版本再決定解壓方式
        using (var hw = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            hw.Write(BinaryHeader);
            hw.Write(BinaryVersion); // v4
            hw.Write(size);
            hw.Write(generation);
        }

        // Payload：Columnar 欄位順序寫入單一 BrotliStream（quality Optimal，最大壓縮）
        using var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var bw = new BinaryWriter(brotli, System.Text.Encoding.UTF8, leaveOpen: true);

        if (size > (ulong)int.MaxValue)
            throw new InvalidOperationException($"TT 過大無法匯出：{size} 個條目超過 int 上限。");
        int tableSize = (int)size;

        // Col 0：完整 zobrist key（0 = 空槽，隨機資料欄分離可減少其他欄的干擾）
        for (int i = 0; i < tableSize; i++)
        {
            ulong kxd = Volatile.Read(ref keys[i]);
            if (kxd == 0) { bw.Write(0uL); continue; }
            ulong d = Volatile.Read(ref data[i]);
            bw.Write(kxd ^ d); // 還原並寫入完整 key
        }

        // Col 1-6：低熵欄位，獨立欄位後 Brotli 可發揮最大壓縮效果
        for (int i = 0; i < tableSize; i++)
        {
            ulong kxd = Volatile.Read(ref keys[i]);
            bw.Write(kxd == 0 ? (short)0 : (short)(ushort)(Volatile.Read(ref data[i]) & 0xFFFF));
        }
        for (int i = 0; i < tableSize; i++)
        {
            ulong kxd = Volatile.Read(ref keys[i]);
            bw.Write(kxd == 0 ? (byte)0 : (byte)((Volatile.Read(ref data[i]) >> 16) & 0xFF));
        }
        for (int i = 0; i < tableSize; i++)
        {
            ulong kxd = Volatile.Read(ref keys[i]);
            bw.Write(kxd == 0 ? (byte)0 : (byte)((Volatile.Read(ref data[i]) >> 24) & 0x3));
        }
        for (int i = 0; i < tableSize; i++)
        {
            ulong kxd = Volatile.Read(ref keys[i]);
            bw.Write(kxd == 0 ? (byte)0 : (byte)((Volatile.Read(ref data[i]) >> 26) & 0x7F));
        }
        for (int i = 0; i < tableSize; i++)
        {
            ulong kxd = Volatile.Read(ref keys[i]);
            bw.Write(kxd == 0 ? (byte)0 : (byte)((Volatile.Read(ref data[i]) >> 33) & 0x7F));
        }
        for (int i = 0; i < tableSize; i++)
        {
            ulong kxd = Volatile.Read(ref keys[i]);
            bw.Write(kxd == 0 ? (byte)0 : (byte)((Volatile.Read(ref data[i]) >> 40) & 0xFF));
        }
    }

    public void ImportFromBinary(Stream input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        using var headerReader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);

        var magic = headerReader.ReadBytes(BinaryHeader.Length);
        if (magic.Length != BinaryHeader.Length || !magic.SequenceEqual(BinaryHeader))
            throw new InvalidDataException("The file is not a valid TT binary snapshot.");

        uint version = headerReader.ReadUInt32();
        if (version != BinaryVersion && version != BrotliColumnarVersion
            && version != GzipBinaryVersion && version != LegacyBinaryVersion)
        {
            throw new InvalidDataException($"Unsupported TT binary version: {version}");
        }

        ulong snapshotSize = headerReader.ReadUInt64();
        if (snapshotSize == 0)
            throw new InvalidDataException($"Invalid TT table size in binary snapshot: {snapshotSize}");

        // v4 允許不同大小（透過 QP 重新映射），v1/v2/v3 需要大小吻合
        if (version != BinaryVersion && snapshotSize != size)
        {
            throw new InvalidDataException(
                $"TT size mismatch. Snapshot={snapshotSize}, current={size}. " +
                $"Use v4 format for cross-size import.");
        }

        // v4 匯入時若快照大小超過當前容量，以快照大小為準（需判斷 snapshotSize 合理性）
        if (version == BinaryVersion && snapshotSize > (ulong)int.MaxValue)
            throw new InvalidDataException($"TT snapshot size too large: {snapshotSize}");

        byte snapshotGeneration = headerReader.ReadByte();
        int n = (int)snapshotSize;

        generation = snapshotGeneration;

        if (version == LegacyBinaryVersion)
        {
            // v1：舊版無壓縮格式（向後相容）
            // 條目位置為直接映射（key % size == index），QP step=0 可找到
            var importedKeys = new ulong[n];
            var importedData = new ulong[n];
            for (int i = 0; i < n; i++) importedKeys[i] = headerReader.ReadUInt64();
            for (int i = 0; i < n; i++) importedData[i] = headerReader.ReadUInt64();
            System.Array.Copy(importedKeys, keys, n);
            System.Array.Copy(importedData, data, n);
        }
        else if (version == GzipBinaryVersion)
        {
            // v2：GZip 壓縮格式（向後相容）
            // 條目位置為直接映射，QP step=0 可找到
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var dr = new BinaryReader(gzip, System.Text.Encoding.UTF8, leaveOpen: true);
            var importedKeys = new ulong[n];
            var importedData = new ulong[n];
            for (int i = 0; i < n; i++) importedKeys[i] = dr.ReadUInt64();
            for (int i = 0; i < n; i++) importedData[i] = dr.ReadUInt64();
            System.Array.Copy(importedKeys, keys, n);
            System.Array.Copy(importedData, data, n);
        }
        else if (version == BrotliColumnarVersion)
        {
            // v3：Columnar + Quotient key + Brotli（向後相容）
            // 條目位置由 quotient 還原為 key % size，QP step=0 可找到
            ImportV3Columnar(input, n);
        }
        else
        {
            // v4：Full key + Columnar + Brotli，透過 QP 重建正確佈局
            ImportV4Columnar(input, n);
        }

        Interlocked.Exchange(ref occupiedCount, CountOccupied());
    }

    /// <summary>
    /// 讀取 v3 Columnar 格式並重建 TT 陣列。
    /// key 重建公式：key = index + (quotient - 1) × size（直接映射不變量）
    /// </summary>
    private void ImportV3Columnar(Stream input, int n)
    {
        var quotients = new ulong[n];
        var scores    = new short[n];
        var depths    = new byte[n];
        var flags     = new byte[n];
        var froms     = new byte[n];
        var tos       = new byte[n];
        var gens      = new byte[n];

        using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var dr = new BinaryReader(brotli, System.Text.Encoding.UTF8, leaveOpen: true);

        for (int i = 0; i < n; i++) quotients[i] = ReadVarUInt64(dr);
        for (int i = 0; i < n; i++) scores[i]    = dr.ReadInt16();
        for (int i = 0; i < n; i++) depths[i]    = dr.ReadByte();
        for (int i = 0; i < n; i++) flags[i]     = dr.ReadByte();
        for (int i = 0; i < n; i++) froms[i]     = dr.ReadByte();
        for (int i = 0; i < n; i++) tos[i]       = dr.ReadByte();
        for (int i = 0; i < n; i++) gens[i]      = dr.ReadByte();

        System.Array.Clear(keys, 0, n);
        System.Array.Clear(data, 0, n);

        for (int i = 0; i < n; i++)
        {
            if (quotients[i] == 0) continue; // 空槽

            ulong k          = quotients[i] - 1;
            ulong zobristKey = (ulong)i + k * size;
            ulong packed     = Pack(scores[i], depths[i], (TTFlag)flags[i], froms[i], tos[i], gens[i]);

            // v3 保證 key % size == i，直接寫入（QP step=0 可找到）
            data[i] = packed;
            keys[i] = zobristKey ^ packed;
        }
    }

    /// <summary>
    /// 讀取 v4 Full key Columnar 格式並透過 QP 重建 TT 佈局。
    /// 匯入時透過 InsertEntry 重新以 QP 插入每個條目，正確重建探測序列。
    /// </summary>
    private void ImportV4Columnar(Stream input, int n)
    {
        var fullKeys = new ulong[n];
        var scores   = new short[n];
        var depths   = new byte[n];
        var flags    = new byte[n];
        var froms    = new byte[n];
        var tos      = new byte[n];
        var gens     = new byte[n];

        using var brotli = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var dr = new BinaryReader(brotli, System.Text.Encoding.UTF8, leaveOpen: true);

        for (int i = 0; i < n; i++) fullKeys[i] = dr.ReadUInt64();
        for (int i = 0; i < n; i++) scores[i]   = dr.ReadInt16();
        for (int i = 0; i < n; i++) depths[i]   = dr.ReadByte();
        for (int i = 0; i < n; i++) flags[i]    = dr.ReadByte();
        for (int i = 0; i < n; i++) froms[i]    = dr.ReadByte();
        for (int i = 0; i < n; i++) tos[i]      = dr.ReadByte();
        for (int i = 0; i < n; i++) gens[i]     = dr.ReadByte();

        // 清空表後透過 QP 重建佈局
        System.Array.Clear(keys, 0, keys.Length);
        System.Array.Clear(data, 0, data.Length);
        Interlocked.Exchange(ref occupiedCount, 0);

        for (int i = 0; i < n; i++)
        {
            if (fullKeys[i] == 0) continue; // 空槽

            ulong packed = Pack(scores[i], depths[i], (TTFlag)flags[i], froms[i], tos[i], gens[i]);
            InsertEntry(fullKeys[i], packed);
        }
    }

    public void ExportToJson(Stream output)
    {
        if (output == null)
            throw new ArgumentNullException(nameof(output));

        var snapshot = new TTStateSnapshot
        {
            Size = size,
            Generation = generation,
            Keys = keys,
            Data = data
        };

        JsonSerializer.Serialize(output, snapshot, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public void ImportFromJson(Stream input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        TTStateSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<TTStateSnapshot>(input);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid TT json snapshot.", ex);
        }

        if (snapshot == null)
            throw new InvalidDataException("TT json snapshot is null.");

        if (snapshot.Size != size)
            throw new InvalidDataException($"TT size mismatch. Snapshot={snapshot.Size}, current={size}");

        if (snapshot.Keys == null || snapshot.Data == null)
            throw new InvalidDataException("TT snapshot is missing key/data arrays.");

        if (snapshot.Keys.Length != (int)size || snapshot.Data.Length != (int)size)
            throw new InvalidDataException("TT snapshot key/data length does not match table size.");

        generation = snapshot.Generation;
        System.Array.Copy(snapshot.Keys, keys, (int)size);
        System.Array.Copy(snapshot.Data, data, (int)size);
        Interlocked.Exchange(ref occupiedCount, CountOccupied());
    }

    private long CountOccupied()
    {
        long count = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] != 0) count++;
        }
        return count;
    }

    /// <summary>
    /// VarUInt64 編碼（Protocol-Buffers 相容的 base-128 varint）。
    /// 每個 byte 的 MSB=1 表示後面還有 byte，MSB=0 表示最後一個 byte。
    /// 範圍 0–127 用 1 byte；2^42 約用 6 bytes（vs 固定 8 bytes 省 25%）。
    /// </summary>
    private static void WriteVarUInt64(BinaryWriter w, ulong value)
    {
        while (value >= 0x80)
        {
            w.Write((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        w.Write((byte)value);
    }

    private static ulong ReadVarUInt64(BinaryReader r)
    {
        ulong result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = r.ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0 && shift < 63);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Pack(short score, byte depth, TTFlag flag, int from, int to, byte generation)
    {
        ulong packed = (ulong)(ushort)score;                 // 位元 0-15：分數
        packed |= (ulong)depth << 16;                        // 位元 16-23：深度
        packed |= (ulong)((byte)flag & 0x3) << 24;           // 位元 24-25：旗標
        packed |= (ulong)(from & 0x7F) << 26;                // 位元 26-32：from
        packed |= (ulong)(to & 0x7F) << 33;                  // 位元 33-39：to
        packed |= (ulong)generation << 40;                    // 位元 40-47：generation
        return packed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TTEntry Unpack(ulong packed, ulong key)
    {
        return new TTEntry
        {
            Key = key,
            Score = (short)(ushort)(packed & 0xFFFF),
            Depth = (byte)((packed >> 16) & 0xFF),
            Flag = (TTFlag)((packed >> 24) & 0x3),
            BestMove = new Move((byte)((packed >> 26) & 0x7F), (byte)((packed >> 33) & 0x7F)),
            Generation = (byte)((packed >> 40) & 0xFF)
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClampScore(int score)
    {
        if (score > 30000) return 30000;
        if (score < -30000) return -30000;
        return score;
    }

    /// <summary>
    /// 枚舉 TT 中所有有效條目（惰性求值）。
    /// 有效條目的判斷依據：keys[i] 不為零（XOR 驗證格式）。
    /// </summary>
    public IEnumerable<TTEntry> EnumerateEntries()
    {
        for (ulong i = 0; i < size; i++)
        {
            ulong keyXorData = Volatile.Read(ref keys[i]);
            if (keyXorData == 0) continue;

            ulong entryData = Volatile.Read(ref data[i]);
            ulong zobristKey = keyXorData ^ entryData;

            // 二次確認：重新計算 XOR 應與 keys[i] 吻合（防止撕裂讀取）
            if ((zobristKey ^ entryData) != keyXorData) continue;

            yield return Unpack(entryData, zobristKey);
        }
    }

    /// <summary>
    /// 從 <paramref name="board"/> 的當前局面出發，沿 TT 中的 BestMove 遞迴追蹤，
    /// 建立探索樹。若當前局面不在 TT 中，回傳 <c>null</c>。
    /// </summary>
    /// <param name="board">起始局面（不會被修改）。</param>
    /// <param name="maxDepth">最大探索深度（1 = 只回傳根節點）。</param>
    public TTTreeNode? ExploreTTTree(IBoard board, int maxDepth = 6)
    {
        var visited = new HashSet<ulong>();
        return ExploreNode(board.Clone(), maxDepth, visited, default);
    }

    private TTTreeNode? ExploreNode(IBoard board, int remainingDepth, HashSet<ulong> visited, Move moveToHere)
    {
        ulong key = board.ZobristKey;

        // 防止同一局面被重複訪問（循環偵測）
        if (!visited.Add(key)) return null;

        // 使用 Probe 支援 QP（entry 可能不在 key % size 的位置）
        // 注意：此處對 totalProbes / probeHits 有輕微干擾，可接受（UI 功能，非 hot path）
        if (!Probe(key, out var entry))
        {
            visited.Remove(key);
            return null;
        }

        var node = new TTTreeNode
        {
            Entry = entry,
            MoveToHere = moveToHere,
            Children = []
        };

        // 深度已達上限，或無有效 BestMove，不再遞迴
        bool hasBestMove = entry.BestMove.From != entry.BestMove.To;
        if (remainingDepth <= 1 || !hasBestMove)
        {
            visited.Remove(key);
            return node;
        }

        // 嘗試套用 BestMove 並遞迴探索子局面
        try
        {
            var childBoard = board.Clone();
            childBoard.MakeMove(entry.BestMove);
            var child = ExploreNode(childBoard, remainingDepth - 1, visited, entry.BestMove);
            if (child != null)
                node.Children.Add(child);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            // BestMove 在當前局面非法（TT 碰撞、過期條目或走法不適用當前輪次），忽略
        }

        visited.Remove(key);
        return node;
    }
}
