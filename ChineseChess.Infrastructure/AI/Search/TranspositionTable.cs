using ChineseChess.Domain.Entities;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ChineseChess.Infrastructure.AI.Search;

public enum TTFlag : byte
{
    None = 0,
    Exact = 1,
    LowerBound = 2,
    UpperBound = 3
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TTEntry
{
    public ulong Key;
    public short Score;
    public byte Depth;
    public TTFlag Flag;
    public Move BestMove;
    public byte Generation;
}

/// <summary>
/// 使用 XOR 驗證技巧的 lock-free transposition table。
/// 以兩個對齊的 ulong 陣列（_keys 與 _data）確保 x64 上 8 bytes 原子性讀寫。
/// _keys[i] 存放 (zobristKey ^ packedData)，可偵測到被撕裂讀取的狀況。
/// </summary>
public class TranspositionTable
{
    private readonly ulong[] _keys;
    private readonly ulong[] _data;
    private readonly ulong _size;
    private byte _generation;

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

    public byte Generation => _generation;

    public void NewGeneration()
    {
        _generation++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Probe(ulong key, out TTEntry entry)
    {
        ulong index = key % _size;
        ulong storedKey = Volatile.Read(ref _keys[index]);
        ulong storedData = Volatile.Read(ref _data[index]);

        if ((storedKey ^ storedData) == key)
        {
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

        Volatile.Write(ref _data[index], packed);
        Volatile.Write(ref _keys[index], key ^ packed);
    }

    public void Clear()
    {
        System.Array.Clear(_keys, 0, _keys.Length);
        System.Array.Clear(_data, 0, _data.Length);
        _generation = 0;
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
}
