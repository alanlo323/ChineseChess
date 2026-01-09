using ChineseChess.Domain.Entities;
using System.Runtime.InteropServices;

namespace ChineseChess.Infrastructure.AI.Search;

public enum TTFlag : byte
{
    None = 0,
    Exact = 1,
    LowerBound = 2, // Beta Cutoff (fail-high) -> Score is at least this
    UpperBound = 3  // Alpha Cutoff (fail-low) -> Score is at most this
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct TTEntry
{
    public ulong Key;
    public short Score;
    public byte Depth;
    public TTFlag Flag;
    public Move BestMove;
}

public class TranspositionTable
{
    private readonly TTEntry[] _entries;
    private readonly ulong _size;

    public TranspositionTable(int sizeMb)
    {
        // Calculate count based on struct size
        // Size = 8+2+1+1+ (Move: 1+1+4 = 6) = ~18 bytes.
        // Let's assume Move is 8 bytes (padded) or pack it.
        // Move is struct { byte, byte, int } = 8 bytes roughly.
        // Total ~20 bytes.
        
        // Use a fixed size for simplicity for now: 1M entries
        _size = 1024 * 1024; 
        _entries = new TTEntry[_size];
    }

    public bool Probe(ulong key, out TTEntry entry)
    {
        ulong index = key % _size;
        entry = _entries[index];
        return entry.Key == key;
    }

    public void Store(ulong key, int score, int depth, TTFlag flag, Move bestMove)
    {
        ulong index = key % _size;
        
        // Simple replacement scheme: Always replace if depth is greater or equal, or collision?
        // Or Always replace.
        // Standard: Depth-preferred or Always-Replace.
        
        // Storing "mate" scores needs adjustment if they are relative to root.
        // Here assuming raw score for simplicity in plan.

        _entries[index] = new TTEntry
        {
            Key = key,
            Score = (short)ClampScore(score),
            Depth = (byte)depth,
            Flag = flag,
            BestMove = bestMove
        };
    }

    public void Clear()
    {
        System.Array.Clear(_entries, 0, _entries.Length);
    }
    
    private int ClampScore(int score)
    {
        if (score > 30000) return 30000;
        if (score < -30000) return -30000;
        return score;
    }
}
