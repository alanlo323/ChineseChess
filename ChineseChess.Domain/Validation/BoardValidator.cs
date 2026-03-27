using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System.Collections.Generic;

namespace ChineseChess.Domain.Validation;

/// <summary>
/// 局面合法性驗證：
/// 1. 紅方帥恰好 1 個，且在紅方九宮內（row 7-9，col 3-5）
/// 2. 黑方將恰好 1 個，且在黑方九宮內（row 0-2，col 3-5）
/// 3. 將帥不面對面（同列無阻擋，即「飛將」）
/// </summary>
public static class BoardValidator
{
    // 紅方九宮：row 7-9，col 3-5（index = row*9 + col）
    private const int RedPalaceRowMin = 7;
    private const int RedPalaceRowMax = 9;

    // 黑方九宮：row 0-2，col 3-5
    private const int BlackPalaceRowMin = 0;
    private const int BlackPalaceRowMax = 2;

    // 九宮列範圍（兩方相同）
    private const int PalaceColMin = 3;
    private const int PalaceColMax = 5;

    private const int BoardWidth = 9;
    private const int BoardSize = 90;

    public static BoardValidationResult Validate(IBoard board)
    {
        var errors = new List<string>();

        ValidateKings(board, errors);

        if (errors.Count == 0)
        {
            ValidateFlyingKings(board, errors);
        }

        return new BoardValidationResult(errors);
    }

    private static void ValidateKings(IBoard board, List<string> errors)
    {
        int redKingCount = 0;
        int blackKingCount = 0;
        bool redKingInPalace = true;
        bool blackKingInPalace = true;

        for (int i = 0; i < BoardSize; i++)
        {
            var piece = board.GetPiece(i);
            if (piece.Type != PieceType.King) continue;

            int row = i / BoardWidth;
            int col = i % BoardWidth;

            if (piece.Color == PieceColor.Red)
            {
                redKingCount++;
                if (!IsInRedPalace(row, col)) redKingInPalace = false;
            }
            else
            {
                blackKingCount++;
                if (!IsInBlackPalace(row, col)) blackKingInPalace = false;
            }
        }

        if (redKingCount == 0)
            errors.Add("紅方帥不存在，局面非法。");
        else if (redKingCount > 1)
            errors.Add($"紅方帥數量為 {redKingCount}，局面非法（應恰好 1 個）。");
        else if (!redKingInPalace)
            errors.Add("紅方帥不在九宮內，局面非法。");

        if (blackKingCount == 0)
            errors.Add("黑方將不存在，局面非法。");
        else if (blackKingCount > 1)
            errors.Add($"黑方將數量為 {blackKingCount}，局面非法（應恰好 1 個）。");
        else if (!blackKingInPalace)
            errors.Add("黑方將不在九宮內，局面非法。");
    }

    private static void ValidateFlyingKings(IBoard board, List<string> errors)
    {
        int redKingIndex = -1;
        int blackKingIndex = -1;

        for (int i = 0; i < BoardSize; i++)
        {
            var piece = board.GetPiece(i);
            if (piece.Type != PieceType.King) continue;
            if (piece.Color == PieceColor.Red) redKingIndex = i;
            else blackKingIndex = i;
        }

        if (redKingIndex < 0 || blackKingIndex < 0) return;

        int redCol = redKingIndex % BoardWidth;
        int blackCol = blackKingIndex % BoardWidth;

        // 不在同列，不觸發飛將
        if (redCol != blackCol) return;

        int redRow = redKingIndex / BoardWidth;
        int blackRow = blackKingIndex / BoardWidth;

        // 掃描兩將之間是否有任何棋子阻擋
        int minRow = System.Math.Min(redRow, blackRow);
        int maxRow = System.Math.Max(redRow, blackRow);

        for (int row = minRow + 1; row < maxRow; row++)
        {
            int index = row * BoardWidth + redCol;
            if (!board.GetPiece(index).IsNone) return; // 有阻擋，合法
        }

        errors.Add("將帥面對面（飛將），局面非法。");
    }

    private static bool IsInRedPalace(int row, int col)
        => row >= RedPalaceRowMin && row <= RedPalaceRowMax
        && col >= PalaceColMin && col <= PalaceColMax;

    private static bool IsInBlackPalace(int row, int col)
        => row >= BlackPalaceRowMin && row <= BlackPalaceRowMax
        && col >= PalaceColMin && col <= PalaceColMax;
}
