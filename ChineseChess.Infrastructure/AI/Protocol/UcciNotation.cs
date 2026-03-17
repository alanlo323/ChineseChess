using ChineseChess.Domain.Entities;
using System;

namespace ChineseChess.Infrastructure.AI.Protocol;

/// <summary>
/// UCI / UCCI 棋盤座標與走法字串轉換工具。
///
/// 座標規則：
///   Board 索引：index = row * 9 + col
///     row 0 = 棋盤頂部（黑方底線）
///     row 9 = 棋盤底部（紅方底線）
///   UCI/UCCI 格式（如 "h2e2"）：
///     file: 'a'–'i' = col 0–8（方向相同）
///     rank: 0 = 紅方底（board row 9），9 = 黑方底（board row 0）
///     ucciRank = 9 - boardRow
///     boardRow  = 9 - ucciRank
/// </summary>
public static class UcciNotation
{
    /// <summary>將棋盤索引轉換為 UCI/UCCI 格子字串（例如 85 → "e0"，4 → "e9"）。</summary>
    public static string IndexToSquare(int index)
    {
        int row = index / 9;
        int col = index % 9;
        char file = (char)('a' + col);
        int rank = 9 - row;
        return $"{file}{rank}";
    }

    /// <summary>將 UCI/UCCI 格子字串轉換為棋盤索引（例如 "a9" → 0，"e0" → 85）。</summary>
    public static int SquareToIndex(string square)
    {
        if (square.Length != 2)
            throw new ArgumentException($"不合法的格子字串：{square}");

        char file = square[0];
        if (file < 'a' || file > 'i')
            throw new ArgumentException($"不合法的 file：{file}（應為 a–i）");

        if (!char.IsDigit(square[1]))
            throw new ArgumentException($"不合法的 rank：{square[1]}（應為 0–9）");

        int col = file - 'a';
        int rank = square[1] - '0';
        int row = 9 - rank;
        return row * 9 + col;
    }

    /// <summary>將 Move 轉換為 UCI/UCCI 走法字串（例如 Move(70,67) → "h2e2"）。</summary>
    public static string MoveToUcci(Move move)
        => IndexToSquare(move.From) + IndexToSquare(move.To);

    /// <summary>將 UCI/UCCI 走法字串轉換為 Move（例如 "h2e2" → Move(70,67)）。</summary>
    public static Move UcciToMove(string ucci)
    {
        if (ucci == null || ucci.Length != 4)
            throw new ArgumentException($"不合法的 UCI/UCCI 走法：{ucci ?? "(null)"}（應為 4 個字元，如 h2e2）");

        int from = SquareToIndex(ucci[..2]);
        int to   = SquareToIndex(ucci[2..]);
        return new Move(from, to);
    }
}
