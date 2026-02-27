using System.Collections.Generic;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Domain.Entities;

public interface IBoard
{
    Piece GetPiece(int index);
    PieceColor Turn { get; }
    ulong ZobristKey { get; }
    
    // 走法產生
    IEnumerable<Move> GenerateLegalMoves();
    
    // 狀態變更
    void MakeMove(Move move);
    void UnmakeMove(Move move);
    void MakeNullMove();
    void UnmakeNullMove();
    bool TryGetLastMove(out Move move);
    
    // 狀態查詢
    bool IsCheck(PieceColor color);
    bool IsCheckmate(PieceColor color);
    
    // 工具方法
    string ToFen();
    IBoard Clone();
}
