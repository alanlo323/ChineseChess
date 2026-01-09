using System.Collections.Generic;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Domain.Entities;

public interface IBoard
{
    Piece GetPiece(int index);
    PieceColor Turn { get; }
    ulong ZobristKey { get; }
    
    // Move Generation
    IEnumerable<Move> GenerateLegalMoves();
    
    // State Manipulation
    void MakeMove(Move move);
    void UnmakeMove(Move move); // Or Undo() if we track history internally
    
    // Status
    bool IsCheck(PieceColor color);
    bool IsCheckmate(PieceColor color);
    
    // Utilities
    string ToFen();
    IBoard Clone();
}
