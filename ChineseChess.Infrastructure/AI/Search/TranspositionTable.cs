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
/// Lock-free transposition table using the XOR verification trick.
/// Two aligned ulong arrays (_keys and _data) ensure atomic 8-byte reads/writes on x64.
/// _keys[i] stores (zobristKey ^ packedData) so that torn reads are detected.
/// </summary>
public class TranspositionTable
{
    private readonly ulong[] _keys;
    private readonly ulong[] _data;
    private readonly ulong _size;
    private byte _generation;

    // Pack layout (64 bits):
    //   bits  0-15 : score       (16 bits, signed via cast)
    //   bits 16-23 : depth       (8 bits)
    //   bits 24-25 : flag        (2 bits)
    //   bits 26-32 : from        (7 bits, max 89)
    //   bits 33-39 : to          (7 bits, max 89)
    //   bits 40-47 : generation  (8 bits)

    public TranspositionTable(int sizeMb)
    {
        long bytes = (long)sizeMb * 1024 * 1024;
        long entrySize = 2 * sizeof(ulong); // key + data
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
        ulong packed = (ulong)(ushort)score;                 // bits  0-15
        packed |= (ulong)depth << 16;                        // bits 16-23
        packed |= (ulong)((byte)flag & 0x3) << 24;           // bits 24-25
        packed |= (ulong)(from & 0x7F) << 26;                // bits 26-32
        packed |= (ulong)(to & 0x7F) << 33;                  // bits 33-39
        packed |= (ulong)generation << 40;                    // bits 40-47
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
