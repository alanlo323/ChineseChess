using ChineseChess.Domain.Enums;
using System;

namespace ChineseChess.Domain.Entities;

public readonly struct Piece : IEquatable<Piece>
{
    public PieceColor Color { get; }
    public PieceType Type { get; }

    public Piece(PieceColor color, PieceType type)
    {
        Color = color;
        Type = type;
    }

    public static Piece None => new Piece(PieceColor.None, PieceType.None);
    public bool IsNone => Type == PieceType.None;

    public bool Equals(Piece other) => Color == other.Color && Type == other.Type;
    public override bool Equals(object? obj) => obj is Piece other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Color, Type);
    public static bool operator ==(Piece left, Piece right) => left.Equals(right);
    public static bool operator !=(Piece left, Piece right) => !left.Equals(right);

    public override string ToString() => $"{Color} {Type}";
    
    // Character representation (optional helper)
    public char ToChar()
    {
        if (IsNone) return '.';
        // Simple mapping, can be expanded
        return Type switch
        {
            PieceType.King => Color == PieceColor.Red ? 'K' : 'k',
            PieceType.Advisor => Color == PieceColor.Red ? 'A' : 'a',
            PieceType.Elephant => Color == PieceColor.Red ? 'E' : 'e',
            PieceType.Horse => Color == PieceColor.Red ? 'H' : 'h',
            PieceType.Rook => Color == PieceColor.Red ? 'R' : 'r',
            PieceType.Cannon => Color == PieceColor.Red ? 'C' : 'c',
            PieceType.Pawn => Color == PieceColor.Red ? 'P' : 'p',
            _ => '?'
        };
    }
}
