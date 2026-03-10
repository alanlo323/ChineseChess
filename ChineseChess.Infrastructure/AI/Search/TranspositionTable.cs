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
/// 以兩個對齊的 ulong 陣列（_keys 與 _data）確保 x64 上 8 bytes 原子性讀寫。
/// _keys[i] 存放 (zobristKey ^ packedData)，可偵測到被撕裂讀取的狀況。
/// </summary>
public class TranspositionTable
{
    private static readonly byte[] BinaryHeader = "CCTT"u8.ToArray();
    private const uint BinaryVersion = 2u;        // v2：GZip 壓縮 payload
    private const uint LegacyBinaryVersion = 1u;  // v1：舊版無壓縮（向後相容）

    private readonly ulong[] _keys;
    private readonly ulong[] _data;
    private readonly ulong _size;
    private byte _generation;
    private long _totalProbes;
    private long _probeHits;
    private long _occupiedCount;

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
        _size = (ulong)(bytes / entrySize);
        if (_size < 1024) _size = 1024;

        _keys = new ulong[_size];
        _data = new ulong[_size];
        _generation = 0;
    }

    // 私有建構子：直接以條目數建立空表（Clone 專用）
    private TranspositionTable(ulong size)
    {
        _size = Math.Max(size, 1024);
        _keys = new ulong[_size];
        _data = new ulong[_size];
        _generation = 0;
    }

    public byte Generation => _generation;

    public void NewGeneration()
    {
        _generation++;
    }

    /// <summary>
    /// 建立本 TT 的深度複製。複製後兩表彼此獨立，世代與資料跟原表一致，
    /// 但統計（查詢次數、命中）從零開始。
    /// </summary>
    public TranspositionTable Clone()
    {
        var clone = new TranspositionTable(_size);
        System.Array.Copy(_keys, clone._keys, (int)_size);
        System.Array.Copy(_data, clone._data, (int)_size);
        clone._generation = _generation;
        Interlocked.Exchange(ref clone._occupiedCount, Interlocked.Read(ref _occupiedCount));
        return clone;
    }

    /// <summary>
    /// 將 <paramref name="other"/> 中的所有有效條目合併進本表，
    /// 採「深度優先」策略：僅在 other 條目搜尋深度大於本表現有條目時才取代。
    /// 兩表大小可以不同；合併時依 zobrist key 重新計算本表索引。
    /// </summary>
    public void MergeFrom(TranspositionTable other)
    {
        for (ulong i = 0; i < other._size; i++)
        {
            ulong otherKeyXorData = Volatile.Read(ref other._keys[i]);
            ulong otherData       = Volatile.Read(ref other._data[i]);

            if (otherKeyXorData == 0) continue; // 空槽，跳過

            // _keys[i] = zobristKey ^ _data[i] → 還原 zobrist key
            ulong zobristKey = otherKeyXorData ^ otherData;
            byte  otherDepth = (byte)((otherData >> 16) & 0xFF);

            // 計算本表中該 key 的對應槽位
            ulong ourIndex     = zobristKey % _size;
            ulong myKeyXorData = Volatile.Read(ref _keys[ourIndex]);
            ulong myData       = Volatile.Read(ref _data[ourIndex]);

            if (myKeyXorData == 0)
            {
                // 本表槽位為空，直接填入
                Interlocked.Increment(ref _occupiedCount);
                Volatile.Write(ref _data[ourIndex], otherData);
                Volatile.Write(ref _keys[ourIndex], zobristKey ^ otherData);
            }
            else
            {
                // 深度優先替換：只有 other 的深度更大才覆寫
                byte myDepth = (byte)((myData >> 16) & 0xFF);
                if (otherDepth > myDepth)
                {
                    Volatile.Write(ref _data[ourIndex], otherData);
                    Volatile.Write(ref _keys[ourIndex], zobristKey ^ otherData);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Probe(ulong key, out TTEntry entry)
    {
        ulong index = key % _size;
        ulong storedKey = Volatile.Read(ref _keys[index]);
        ulong storedData = Volatile.Read(ref _data[index]);

        Interlocked.Increment(ref _totalProbes);

        if ((storedKey ^ storedData) == key)
        {
            Interlocked.Increment(ref _probeHits);
            entry = Unpack(storedData, key);
            return true;
        }

        entry = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(ulong key, int score, int depth, TTFlag flag, Move bestMove)
    {
        ulong index = key % _size;
        ulong packed = Pack((short)ClampScore(score), (byte)depth, flag, bestMove.From, bestMove.To, _generation);

        // 若舊槽為空（首次寫入），記錄佔用數；多執行緒下允許些微高估
        if (_keys[index] == 0)
            Interlocked.Increment(ref _occupiedCount);

        Volatile.Write(ref _data[index], packed);
        Volatile.Write(ref _keys[index], key ^ packed);
    }

    public void Clear()
    {
        System.Array.Clear(_keys, 0, _keys.Length);
        System.Array.Clear(_data, 0, _data.Length);
        _generation = 0;
        Interlocked.Exchange(ref _totalProbes, 0);
        Interlocked.Exchange(ref _probeHits, 0);
        Interlocked.Exchange(ref _occupiedCount, 0);
    }

    public TTStatistics GetStatistics()
    {
        long probes = Interlocked.Read(ref _totalProbes);
        long hits = Interlocked.Read(ref _probeHits);
        long occupied = Interlocked.Read(ref _occupiedCount);
        return new TTStatistics
        {
            Capacity = _size,
            MemoryMb = _size * 16.0 / (1024 * 1024),
            Generation = _generation,
            TotalProbes = probes,
            Hits = hits,
            HitRate = probes > 0 ? (double)hits / probes : 0.0,
            OccupiedEntries = occupied,
            FillRate = _size > 0 ? (double)occupied / _size : 0.0
        };
    }

    public void ExportToBinary(Stream output)
    {
        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        // 標頭以明文寫入（供匯入時識別版本，再決定是否解壓縮）
        using (var headerWriter = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            headerWriter.Write(BinaryHeader);
            headerWriter.Write(BinaryVersion);
            headerWriter.Write(_size);
            headerWriter.Write(_generation);
        }

        // Payload 以 GZip 壓縮寫入（v2）
        using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var dataWriter = new BinaryWriter(gzip, System.Text.Encoding.UTF8, leaveOpen: true);
        for (int i = 0; i < _keys.Length; i++)
        {
            dataWriter.Write(_keys[i]);
        }
        for (int i = 0; i < _data.Length; i++)
        {
            dataWriter.Write(_data[i]);
        }
        // dataWriter Dispose 後 gzip Flush，gzip Dispose 後將壓縮結尾寫入 output
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
        if (version != BinaryVersion && version != LegacyBinaryVersion)
        {
            throw new InvalidDataException($"Unsupported TT binary version: {version}");
        }

        ulong size = headerReader.ReadUInt64();
        if (size == 0 || size > (ulong)_keys.Length)
        {
            throw new InvalidDataException($"Invalid TT table size in binary snapshot: {size}");
        }

        if (size != _size)
        {
            throw new InvalidDataException($"TT size mismatch. Snapshot={size}, current={_size}");
        }

        byte generation = headerReader.ReadByte();
        int expectedLength = (int)size;

        var keys = new ulong[expectedLength];
        var data = new ulong[expectedLength];

        if (version == LegacyBinaryVersion)
        {
            // v1：舊版無壓縮格式（向後相容）
            for (int i = 0; i < expectedLength; i++) keys[i] = headerReader.ReadUInt64();
            for (int i = 0; i < expectedLength; i++) data[i] = headerReader.ReadUInt64();
        }
        else
        {
            // v2：GZip 壓縮格式
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var dataReader = new BinaryReader(gzip, System.Text.Encoding.UTF8, leaveOpen: true);
            for (int i = 0; i < expectedLength; i++) keys[i] = dataReader.ReadUInt64();
            for (int i = 0; i < expectedLength; i++) data[i] = dataReader.ReadUInt64();
        }

        _generation = generation;
        System.Array.Copy(keys, _keys, expectedLength);
        System.Array.Copy(data, _data, expectedLength);
        Interlocked.Exchange(ref _occupiedCount, CountOccupied());
    }

    public void ExportToJson(Stream output)
    {
        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        var snapshot = new TTStateSnapshot
        {
            Size = _size,
            Generation = _generation,
            Keys = _keys,
            Data = _data
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

        if (snapshot.Size != _size)
        {
            throw new InvalidDataException($"TT size mismatch. Snapshot={snapshot.Size}, current={_size}");
        }

        if (snapshot.Keys == null || snapshot.Data == null)
        {
            throw new InvalidDataException("TT snapshot is missing key/data arrays.");
        }

        if (snapshot.Keys.Length != (int)_size || snapshot.Data.Length != (int)_size)
        {
            throw new InvalidDataException("TT snapshot key/data length does not match table size.");
        }

        _generation = snapshot.Generation;
        System.Array.Copy(snapshot.Keys, _keys, (int)_size);
        System.Array.Copy(snapshot.Data, _data, (int)_size);
        Interlocked.Exchange(ref _occupiedCount, CountOccupied());
    }

    private long CountOccupied()
    {
        long count = 0;
        for (int i = 0; i < _keys.Length; i++)
        {
            if (_keys[i] != 0) count++;
        }
        return count;
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
    /// 有效條目的判斷依據：_keys[i] 不為零（XOR 驗證格式）。
    /// </summary>
    public IEnumerable<TTEntry> EnumerateEntries()
    {
        for (ulong i = 0; i < _size; i++)
        {
            ulong keyXorData = Volatile.Read(ref _keys[i]);
            if (keyXorData == 0) continue;

            ulong data = Volatile.Read(ref _data[i]);
            ulong zobristKey = keyXorData ^ data;

            // 二次確認：重新計算 XOR 應與 _keys[i] 吻合（防止撕裂讀取）
            if ((zobristKey ^ data) != keyXorData) continue;

            yield return Unpack(data, zobristKey);
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

        ulong index = key % _size;
        ulong keyXorData = Volatile.Read(ref _keys[index]);
        ulong data = Volatile.Read(ref _data[index]);

        // 驗證 TT 命中
        if ((keyXorData ^ data) != key)
        {
            visited.Remove(key);
            return null;
        }

        var entry = Unpack(data, key);
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
