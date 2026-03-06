using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System;
using System.Collections.Generic;

namespace ChineseChess.Domain.Helpers;

/// <summary>
/// 象棋走法標準記譜法轉換（如「馬8進7」、「前車退3」）。
/// 列號規則：紅方從右到左 1–9（col 8=1, col 0=9）；黑方從左到右 1–9（col 0=1, col 8=9）。
/// </summary>
public static class MoveNotation
{
    // PieceType: None=0, King=1, Advisor=2, Elephant=3, Horse=4, Rook=5, Cannon=6, Pawn=7
    private static readonly string[] RedNames   = { "?", "帥", "仕", "相", "俥", "傌", "炮", "兵" };
    private static readonly string[] BlackNames = { "?", "將", "士", "象", "車", "馬", "砲", "卒" };

    /// <summary>
    /// 將走法轉換為標準象棋記譜法。
    /// <paramref name="board"/> 必須是走法執行前的棋盤狀態。
    /// </summary>
    public static string ToNotation(Move move, IBoard board)
    {
        var piece = board.GetPiece(move.From);
        if (piece.IsNone) return move.ToString();

        int fromRow = move.From / 9;
        int fromCol = move.From % 9;
        int toRow   = move.To   / 9;
        int toCol   = move.To   % 9;

        bool isRed = piece.Color == PieceColor.Red;

        // 從行棋方視角的列號（1 = 己方右側，9 = 己方左側）
        int fromColNum = isRed ? (9 - fromCol) : (fromCol + 1);
        int toColNum   = isRed ? (9 - toCol)   : (toCol   + 1);

        string pieceName = isRed ? RedNames[(int)piece.Type] : BlackNames[(int)piece.Type];

        // 同列有同色同種棋子時，加前後區分前綴
        string prefix = GetDisambiguationPrefix(move.From, piece, board);

        char direction;
        int  destination;

        if (fromRow == toRow)
        {
            // 橫移
            direction   = '平';
            destination = toColNum;
        }
        else
        {
            bool advancing = isRed ? (toRow < fromRow) : (toRow > fromRow);
            direction = advancing ? '進' : '退';

            // 直線走棋（車、炮、帥/將、兵/卒）：目標為步數
            // 斜向走棋（馬、象、仕）：目標為落點列號
            destination = IsLinearMover(piece.Type)
                ? Math.Abs(toRow - fromRow)
                : toColNum;
        }

        // 有前後前綴時省略起始列號（如「前馬進7」而非「前馬8進7」）
        return string.IsNullOrEmpty(prefix)
            ? $"{pieceName}{fromColNum}{direction}{destination}"
            : $"{prefix}{pieceName}{direction}{destination}";
    }

    // 直線走棋：目標用步數；斜向走棋：目標用列號
    private static bool IsLinearMover(PieceType type) =>
        type is PieceType.King or PieceType.Rook or PieceType.Cannon or PieceType.Pawn;

    /// <summary>
    /// 若同一縱列有兩顆以上同色同種棋子，回傳「前」/「後」（或「前」/「中」/「後」）前綴；否則回傳空字串。
    /// 「前」= 靠近對方陣地的那顆。
    /// </summary>
    private static string GetDisambiguationPrefix(int fromIndex, Piece piece, IBoard board)
    {
        int fromCol = fromIndex % 9;
        int fromRow = fromIndex / 9;
        bool isRed  = piece.Color == PieceColor.Red;

        // 收集同列、同色、同種棋子的所有列（row）
        var sameColRows = new List<int>(4);
        for (int r = 0; r < 10; r++)
        {
            var p = board.GetPiece(r * 9 + fromCol);
            if (p.Color == piece.Color && p.Type == piece.Type)
                sameColRows.Add(r);
        }

        if (sameColRows.Count < 2) return "";

        sameColRows.Sort(); // row 由小到大

        if (sameColRows.Count == 2)
        {
            // 紅方：row 較小 = 前（靠近黑方）；黑方：row 較大 = 前（靠近紅方）
            bool isFront = isRed ? (fromRow == sameColRows[0]) : (fromRow == sameColRows[1]);
            return isFront ? "前" : "後";
        }

        if (sameColRows.Count == 3)
        {
            // 三兵/三卒同列（罕見但合法）
            int rank = isRed
                ? (2 - sameColRows.IndexOf(fromRow))   // 紅：row 越小排名越前
                : sameColRows.IndexOf(fromRow);         // 黑：row 越大排名越前
            return rank switch { 0 => "前", 1 => "中", _ => "後" };
        }

        return ""; // 四顆以上同列同種屬極端情況，暫不處理
    }
}
