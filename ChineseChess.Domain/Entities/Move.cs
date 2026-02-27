using System;

namespace ChineseChess.Domain.Entities;

public readonly struct Move : IEquatable<Move>
{
    public byte From { get; }
    public byte To { get; }
    
    // 用於 Move Ordering（不屬於 move 身分識別核心，但有實作上的用途）
    public int Score { get; } 

    public Move(byte from, byte to, int score = 0)
    {
        From = from;
        To = to;
        Score = score;
    }

    public Move(int from, int to, int score = 0) : this((byte)from, (byte)to, score) { }

    public bool IsNull => From == 0 && To == 0;

    public static Move Null => new Move(0, 0);

    public bool Equals(Move other) => From == other.From && To == other.To;
    public override bool Equals(object? obj) => obj is Move other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(From, To);
    public static bool operator ==(Move left, Move right) => left.Equals(right);
    public static bool operator !=(Move left, Move right) => !left.Equals(right);

    public override string ToString() => $"Move({From} -> {To})";
}
