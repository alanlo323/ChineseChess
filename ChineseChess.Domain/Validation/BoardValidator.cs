using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System.Collections.Generic;

namespace ChineseChess.Domain.Validation;

/// <summary>
/// 局面合法性驗證：
/// 1. 紅方帥恰好 1 個，且在紅方九宮內（row 7-9，col 3-5）
/// 2. 黑方將恰好 1 個，且在黑方九宮內（row 0-2，col 3-5）
/// 3. 仕/士必須在己方九宮對角線位置（每方 5 個合法位）
/// 4. 相/象必須在己方半場田字頂點（每方 7 個合法位）
/// 5. 兵/卒不可在己方起始線後方
/// 6. 各棋子數量不超過初始數量
/// 7. 將帥不面對面（同列無阻擋，即「飛將」）
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

    // ─── 仕/士合法位置（九宮對角線 + 中心） ────────────────────────────
    // 紅仕：(7,3)=66, (7,5)=68, (8,4)=76, (9,3)=84, (9,5)=86
    private static readonly HashSet<int> RedAdvisorPositions = new() { 66, 68, 76, 84, 86 };
    // 黑士：(0,3)=3, (0,5)=5, (1,4)=13, (2,3)=21, (2,5)=23
    private static readonly HashSet<int> BlackAdvisorPositions = new() { 3, 5, 13, 21, 23 };

    // ─── 相/象合法位置（田字頂點，不過河） ──────────────────────────────
    // 紅相：(5,2)=47, (5,6)=51, (7,0)=63, (7,4)=67, (7,8)=71, (9,2)=83, (9,6)=87
    private static readonly HashSet<int> RedElephantPositions = new() { 47, 51, 63, 67, 71, 83, 87 };
    // 黑象：(0,2)=2, (0,6)=6, (2,0)=18, (2,4)=22, (2,8)=26, (4,2)=38, (4,6)=42
    private static readonly HashSet<int> BlackElephantPositions = new() { 2, 6, 18, 22, 26, 38, 42 };

    public static BoardValidationResult Validate(IBoard board)
    {
        var errors = new List<string>();

        ValidateAllPieces(board, errors);

        if (errors.Count == 0)
        {
            ValidateFlyingKings(board, errors);
        }

        return new BoardValidationResult(errors);
    }

    /// <summary>
    /// 驗證單一棋子在指定位置是否合法（僅檢查位置規則，不檢查數量）。
    /// 回傳 null 表示合法，否則回傳錯誤訊息。
    /// </summary>
    public static string? ValidatePlacement(int index, Piece piece)
    {
        if (piece.IsNone) return null;
        if (index < 0 || index >= BoardSize) return "位置超出棋盤範圍。";

        int row = index / BoardWidth;
        int col = index % BoardWidth;

        return piece.Type switch
        {
            PieceType.King => ValidateKingPosition(piece.Color, row, col),
            PieceType.Advisor => ValidateAdvisorPosition(piece.Color, index),
            PieceType.Elephant => ValidateElephantPosition(piece.Color, index),
            PieceType.Pawn => ValidatePawnPosition(piece.Color, row),
            _ => null // 車、馬、炮可在任意位置
        };
    }

    private static string? ValidateKingPosition(PieceColor color, int row, int col)
    {
        if (color == PieceColor.Red)
            return IsInRedPalace(row, col) ? null : "紅方帥必須在九宮內（第 8-10 行，第 4-6 列）。";
        return IsInBlackPalace(row, col) ? null : "黑方將必須在九宮內（第 1-3 行，第 4-6 列）。";
    }

    private static string? ValidateAdvisorPosition(PieceColor color, int index)
    {
        if (color == PieceColor.Red)
            return RedAdvisorPositions.Contains(index) ? null : "紅仕只能在九宮對角線位置。";
        return BlackAdvisorPositions.Contains(index) ? null : "黑士只能在九宮對角線位置。";
    }

    private static string? ValidateElephantPosition(PieceColor color, int index)
    {
        if (color == PieceColor.Red)
            return RedElephantPositions.Contains(index) ? null : "紅相只能在己方半場的田字頂點。";
        return BlackElephantPositions.Contains(index) ? null : "黑象只能在己方半場的田字頂點。";
    }

    private static string? ValidatePawnPosition(PieceColor color, int row)
    {
        if (color == PieceColor.Red)
            return row >= 7 ? "紅兵不可在己方底線後方（第 8-10 行）。" : null;
        return row <= 2 ? "黑卒不可在己方底線後方（第 1-3 行）。" : null;
    }

    // ─── 全盤驗證（位置 + 數量） ──────────────────────────────────────

    private static void ValidateAllPieces(IBoard board, List<string> errors)
    {
        int redKingCount = 0, blackKingCount = 0;
        int redAdvisorCount = 0, blackAdvisorCount = 0;
        int redElephantCount = 0, blackElephantCount = 0;
        int redHorseCount = 0, blackHorseCount = 0;
        int redRookCount = 0, blackRookCount = 0;
        int redCannonCount = 0, blackCannonCount = 0;
        int redPawnCount = 0, blackPawnCount = 0;

        for (int i = 0; i < BoardSize; i++)
        {
            var piece = board.GetPiece(i);
            if (piece.IsNone) continue;

            // 位置驗證（含帥/將九宮檢查）
            var posError = ValidatePlacement(i, piece);
            if (posError != null)
                errors.Add(posError);

            // 計數
            if (piece.Color == PieceColor.Red)
            {
                switch (piece.Type)
                {
                    case PieceType.King: redKingCount++; break;
                    case PieceType.Advisor: redAdvisorCount++; break;
                    case PieceType.Elephant: redElephantCount++; break;
                    case PieceType.Horse: redHorseCount++; break;
                    case PieceType.Rook: redRookCount++; break;
                    case PieceType.Cannon: redCannonCount++; break;
                    case PieceType.Pawn: redPawnCount++; break;
                }
            }
            else
            {
                switch (piece.Type)
                {
                    case PieceType.King: blackKingCount++; break;
                    case PieceType.Advisor: blackAdvisorCount++; break;
                    case PieceType.Elephant: blackElephantCount++; break;
                    case PieceType.Horse: blackHorseCount++; break;
                    case PieceType.Rook: blackRookCount++; break;
                    case PieceType.Cannon: blackCannonCount++; break;
                    case PieceType.Pawn: blackPawnCount++; break;
                }
            }
        }

        // 帥/將數量驗證（位置已由 ValidatePlacement 檢查）
        if (redKingCount == 0)
            errors.Add("紅方帥不存在，局面非法。");
        else if (redKingCount > 1)
            errors.Add($"紅方帥數量為 {redKingCount}，局面非法（應恰好 1 個）。");

        if (blackKingCount == 0)
            errors.Add("黑方將不存在，局面非法。");
        else if (blackKingCount > 1)
            errors.Add($"黑方將數量為 {blackKingCount}，局面非法（應恰好 1 個）。");

        // 其他棋子數量驗證
        ValidateCount(errors, "紅仕", redAdvisorCount, 2);
        ValidateCount(errors, "黑士", blackAdvisorCount, 2);
        ValidateCount(errors, "紅相", redElephantCount, 2);
        ValidateCount(errors, "黑象", blackElephantCount, 2);
        ValidateCount(errors, "紅傌", redHorseCount, 2);
        ValidateCount(errors, "黑馬", blackHorseCount, 2);
        ValidateCount(errors, "紅俥", redRookCount, 2);
        ValidateCount(errors, "黑車", blackRookCount, 2);
        ValidateCount(errors, "紅炮", redCannonCount, 2);
        ValidateCount(errors, "黑砲", blackCannonCount, 2);
        ValidateCount(errors, "紅兵", redPawnCount, 5);
        ValidateCount(errors, "黑卒", blackPawnCount, 5);
    }

    private static void ValidateCount(List<string> errors, string name, int count, int max)
    {
        if (count > max)
            errors.Add($"{name}數量為 {count}，超出上限（最多 {max} 個）。");
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
