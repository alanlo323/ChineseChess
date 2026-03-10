using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// </summary>
public class TranspositionTable
{
    private static readonly byte[] BinaryHeader = "CCTT"u8.ToArray();
    private const uint BinaryVersion      = 3u;  // v3：Columnar + Quotient key + Brotli
    private const uint GzipBinaryVersion  = 2u;  // v2：GZip 壓縮（向後相容讀取）
    private const uint LegacyBinaryVersion = 1u; // v1：無壓縮（向後相容讀取）

    private readonly ulong[] keys;
    private readonly ulong[] data;
    private readonly ulong size;
    private byte generation;
    private long totalProbes;
    private long probeHits;
    private long occupiedCount;

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

    // 私有建構子：直接以條目數建立空表（Clone 專用）
    private TranspositionTable(ulong size)
    {
        this.size = Math.Max(size, 1024);
        keys = new ulong[this.size];
        data = new ulong[this.size];
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
        var clone = new TranspositionTable(size);
        System.Array.Copy(keys, clone.keys, (int)size);
        System.Array.Copy(data, clone.data, (int)size);
        clone.generation = generation;
        Interlocked.Exchange(ref clone.occupiedCount, Interlocked.Read(ref occupiedCount));
        return clone;
    }

    /// <summary>
    /// 將 <paramref name="other"/> 中的所有有效條目合併進本表，
    /// 採「深度優先」策略：僅在 other 條目搜尋深度大於本表現有條目時才取代。
    /// 兩表大小可以不同；合併時依 zobrist key 重新計算本表索引。
    /// </summary>
    public void MergeFrom(TranspositionTable other)
    {
        for (ulong i = 0; i < other.size; i++)
        {
            ulong otherKeyXorData = Volatile.Read(ref other.keys[i]);
            ulong otherData       = Volatile.Read(ref other.data[i]);

            if (otherKeyXorData == 0) continue; // 空槽，跳過

            // keys[i] = zobristKey ^ data[i] → 還原 zobrist key
            ulong zobristKey = otherKeyXorData ^ otherData;
            byte  otherDepth = (byte)((otherData >> 16) & 0xFF);

            // 計算本表中該 key 的對應槽位
            ulong ourIndex     = zobristKey % size;
            ulong myKeyXorData = Volatile.Read(ref keys[ourIndex]);
            ulong myData       = Volatile.Read(ref data[ourIndex]);

            if (myKeyXorData == 0)
            {
                // 本表槽位為空，直接填入
                Interlocked.Increment(ref occupiedCount);
                Volatile.Write(ref data[ourIndex], otherData);
                Volatile.Write(ref keys[ourIndex], zobristKey ^ otherData);
            }
            else
            {
                // 深度優先替換：只有 other 的深度更大才覆寫
                byte myDepth = (byte)((myData >> 16) & 0xFF);
                if (otherDepth > myDepth)
                {
                    Volatile.Write(ref data[ourIndex], otherData);
                    Volatile.Write(ref keys[ourIndex], zobristKey ^ otherData);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Probe(ulong key, out TTEntry entry)
    {
        ulong index = key % size;
        ulong storedKey = Volatile.Read(ref keys[index]);
        ulong storedData = Volatile.Read(ref data[index]);

        Interlocked.Increment(ref totalProbes);

        if ((storedKey ^ storedData) == key)
        {
            Interlocked.Increment(ref probeHits);
            entry = Unpack(storedData, key);
            return true;
        }

        entry = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ulong key, int score, int depth, TTFlag flag, Move bestMove)
    {
        ulong index = key % size;
        ulong packed = Pack((short)ClampScore(score), (byte)depth, flag, bestMove.From, bestMove.To, generation);

        // 若舊槽為空（首次寫入），記錄佔用數；多執行緒下允許些微高估
        if (keys[index] == 0)
            Interlocked.Increment(ref occupiedCount);

        Volatile.Write(ref data[index], packed);
        Volatile.Write(ref keys[index], key ^ packed);
    }

    public void Clear()
    {
        System.Array.Clear(keys, 0, keys.Length);
        System.Array.Clear(data, 0, data.Length);
        generation = 0;
        Interlocked.Exchange(ref totalProbes, 0);
        Interlocked.Exchange(ref probeHits, 0);
        Interlocked.Exchange(ref occupiedCount, 0);
    }

    public TTStatistics GetStatistics()
    {
        long probes = Interlocked.Read(ref totalProbes);
        long hits = Interlocked.Read(ref probeHits);
        long occupied = Interlocked.Read(ref occupiedCount);
        return new TTStatistics
        {
            Capacity = size,
            MemoryMb = size * 16.0 / (1024 * 1024),
            Generation = generation,
            TotalProbes = probes,
            Hits = hits,
            HitRate = probes > 0 ? (double)hits / probes : 0.0,
            OccupiedEntries = occupied,
            FillRate = size > 0 ? (double)occupied / size : 0.0
        };
    }

    /// <summary>
    /// 以 v3 格式（Columnar + Quotient key + Brotli）匯出 TT 快照。
    ///
    /// 欄位佈局（單一 BrotliStream，Columnar 順序）：
    ///   Col 0: Key quotient（VarUInt64，0=空槽，k+1=有效，k = (key-i) / size）
    ///   Col 1: Score  (int16)
    ///   Col 2: Depth  (byte)
    ///   Col 3: Flag   (byte, 0-3)
    ///   Col 4: From   (byte, 0-89)
    ///   Col 5: To     (byte, 0-89)
    ///   Col 6: Gen    (byte)
    ///
    /// Quotient 編碼利用 TT 不變量：Store() 保證 key % size == index，
    /// 因此 key = index + k × size，只需儲存商 k 即可還原完整 key，
    /// 省去 key 的低位元（約 22 bits），降低 key 欄的資料量。
    /// </summary>
    public void ExportToBinary(Stream output)
    {
        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        // 標頭明文寫入，供匯入時讀取版本再決定解壓方式
        using (var hw = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            hw.Write(BinaryHeader);
            hw.Write(BinaryVersion); // v3
            hw.Write(size);
            hw.Write(generation);
        }

        // Payload：Columnar 欄位順序寫入單一 BrotliStream（quality 4，速度/壓縮平衡）
        using var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var bw = new BinaryWriter(brotli, System.Text.Encoding.UTF8, leaveOpen: true);

        int tableSize = (int)size;

        // Col 0：Key quotient（VarUInt64）——隨機資料，欄位分離可減少干擾
        for (int i = 0; i < tableSize; i++)
        {
            ulong kxd = Volatile.Read(ref keys[i]);
            if (kxd == 0) { WriteVarUInt64(bw, 0); continue; }
            ulong d = Volatile.Read(ref data[i]);
            ulong key = kxd ^ d;
            WriteVarUInt64(bw, (key - (ulong)i) / size + 1); // +1 使 0 成為空槽哨兵
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
        // bw Dispose → brotli Flush → brotli Dispose → 壓縮結尾寫入 output
    }

    public void ImportFromBinary(Stream input)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        using var headerReader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);

        var magic = headerReader.ReadBytes(BinaryHeader.Length);
        if (magic.Length != BinaryHeader.Length || !magic.SequenceEqual(BinaryHeader))
        {
            throw new InvalidDataException("The file is not a valid TT binary snapshot.");
        }

        uint version = headerReader.ReadUInt32();
        if (version != BinaryVersion && version != GzipBinaryVersion && version != LegacyBinaryVersion)
        {
            throw new InvalidDataException($"Unsupported TT binary version: {version}");
        }

        ulong snapshotSize = headerReader.ReadUInt64();
        if (snapshotSize == 0 || snapshotSize > (ulong)keys.Length)
        {
            throw new InvalidDataException($"Invalid TT table size in binary snapshot: {snapshotSize}");
        }

        if (snapshotSize != size)
        {
            throw new InvalidDataException($"TT size mismatch. Snapshot={snapshotSize}, current={size}");
        }

        byte snapshotGeneration = headerReader.ReadByte();
        int n = (int)snapshotSize;

        generation = snapshotGeneration;

        if (version == LegacyBinaryVersion)
        {
            // v1：舊版無壓縮格式（向後相容）
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
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var dr = new BinaryReader(gzip, System.Text.Encoding.UTF8, leaveOpen: true);
            var importedKeys = new ulong[n];
            var importedData = new ulong[n];
            for (int i = 0; i < n; i++) importedKeys[i] = dr.ReadUInt64();
            for (int i = 0; i < n; i++) importedData[i] = dr.ReadUInt64();
            System.Array.Copy(importedKeys, keys, n);
            System.Array.Copy(importedData, data, n);
        }
        else
        {
            // v3：Columnar + Quotient key + Brotli
            ImportV3Columnar(input, n);
        }

        Interlocked.Exchange(ref occupiedCount, CountOccupied());
    }

    /// <summary>
    /// 讀取 v3 Columnar 格式並重建 TT 陣列。
    /// 欄位順序與 ExportToBinary v3 一致（quotient → score → depth → flag → from → to → gen）。
    /// key 重建公式：key = index + (quotient - 1) × size
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

            data[i] = packed;
            keys[i] = zobristKey ^ packed;
        }
    }

    public void ExportToJson(Stream output)
    {
        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

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
        {
            throw new ArgumentNullException(nameof(input));
        }

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
        {
            throw new InvalidDataException("TT json snapshot is null.");
        }

        if (snapshot.Size != size)
        {
            throw new InvalidDataException($"TT size mismatch. Snapshot={snapshot.Size}, current={size}");
        }

        if (snapshot.Keys == null || snapshot.Data == null)
        {
            throw new InvalidDataException("TT snapshot is missing key/data arrays.");
        }

        if (snapshot.Keys.Length != (int)size || snapshot.Data.Length != (int)size)
        {
            throw new InvalidDataException("TT snapshot key/data length does not match table size.");
        }

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

        ulong index = key % size;
        ulong keyXorData = Volatile.Read(ref keys[index]);
        ulong entryData = Volatile.Read(ref data[index]);

        // 驗證 TT 命中
        if ((keyXorData ^ entryData) != key)
        {
            visited.Remove(key);
            return null;
        }

        var entry = Unpack(entryData, key);
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
