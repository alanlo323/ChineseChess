using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System;
using System.Linq;

namespace ChineseChess.Infrastructure.AI.Evaluators;

public class HandcraftedEvaluator : IEvaluator
{
    private const int BoardSize = 90;
    private const int BoardWidth = 9;
    private const int BoardHeight = 10;

    private static readonly int[] PieceValues =
    {
        0,      // None
        10000,  // King
        120,    // Advisor
        120,    // Elephant
        270,    // Horse
        600,    // Rook
        285,    // Cannon
        30      // Pawn (base — PST adds positional value)
    };

    public int Evaluate(IBoard board)
    {
        int score = 0;

        int redKingIndex = -1, blackKingIndex = -1;
        int redAdvisors = 0, blackAdvisors = 0;
        int redElephants = 0, blackElephants = 0;
        int redRookCount = 0, blackRookCount = 0;
        int redRook1 = -1, redRook2 = -1;
        int blackRook1 = -1, blackRook2 = -1;

        for (int i = 0; i < BoardSize; i++)
        {
            var p = board.GetPiece(i);
            if (p.IsNone) continue;

            int sign = p.Color == PieceColor.Red ? 1 : -1;

            // Material
            score += sign * PieceValues[(int)p.Type];

            // PST
            score += sign * PieceSquareTables.GetScore(p.Type, p.Color, i);

            switch (p.Type)
            {
                case PieceType.King:
                    if (p.Color == PieceColor.Red) redKingIndex = i;
                    else blackKingIndex = i;
                    break;
                case PieceType.Advisor:
                    if (p.Color == PieceColor.Red) redAdvisors++;
                    else blackAdvisors++;
                    break;
                case PieceType.Elephant:
                    if (p.Color == PieceColor.Red) redElephants++;
                    else blackElephants++;
                    break;
                case PieceType.Rook:
                    if (p.Color == PieceColor.Red)
                    {
                        if (redRookCount == 0) redRook1 = i; else redRook2 = i;
                        redRookCount++;
                    }
                    else
                    {
                        if (blackRookCount == 0) blackRook1 = i; else blackRook2 = i;
                        blackRookCount++;
                    }
                    break;
            }
        }

        // --- King Safety ---
        score += EvaluateKingSafety(board, PieceColor.Red, redKingIndex, redAdvisors, redElephants);
        score -= EvaluateKingSafety(board, PieceColor.Black, blackKingIndex, blackAdvisors, blackElephants);

        // --- Piece Structure ---
        score += EvaluateRookStructure(board, PieceColor.Red, redRook1, redRook2, redRookCount);
        score -= EvaluateRookStructure(board, PieceColor.Black, blackRook1, blackRook2, blackRookCount);

        // --- Mobility (lightweight: count legal moves for side to move) ---
        int mobility = board.GenerateLegalMoves().Count();
        score += (board.Turn == PieceColor.Red ? 1 : -1) * mobility * 2;

        // Return from side-to-move perspective
        return board.Turn == PieceColor.Red ? score : -score;
    }

    private static int EvaluateKingSafety(IBoard board, PieceColor color, int kingIndex,
        int advisorCount, int elephantCount)
    {
        if (kingIndex < 0) return 0;

        int bonus = 0;

        // Penalty for missing palace defenders
        if (advisorCount < 2) bonus -= (2 - advisorCount) * 20;
        if (elephantCount < 2) bonus -= (2 - elephantCount) * 10;

        // Exposed king penalty: check if king's file is open toward the enemy
        int kingCol = kingIndex % BoardWidth;
        int kingRow = kingIndex / BoardWidth;
        int direction = color == PieceColor.Red ? -1 : 1;
        bool exposed = true;
        for (int r = kingRow + direction; r >= 0 && r < BoardHeight; r += direction)
        {
            var p = board.GetPiece(r * BoardWidth + kingCol);
            if (!p.IsNone)
            {
                exposed = false;
                break;
            }
        }
        if (exposed) bonus -= 40;

        return bonus;
    }

    private static int EvaluateRookStructure(IBoard board, PieceColor color,
        int rook1, int rook2, int rookCount)
    {
        if (rookCount == 0) return 0;

        int bonus = 0;

        // Connected rooks bonus (same rank or file)
        if (rookCount == 2 && rook1 >= 0 && rook2 >= 0)
        {
            int r1Row = rook1 / BoardWidth, r1Col = rook1 % BoardWidth;
            int r2Row = rook2 / BoardWidth, r2Col = rook2 % BoardWidth;
            if (r1Row == r2Row || r1Col == r2Col) bonus += 15;
        }

        // Rook on open file bonus (no friendly pawn in same column)
        for (int ri = 0; ri < rookCount; ri++)
        {
            int rookIdx = ri == 0 ? rook1 : rook2;
            if (rookIdx < 0) continue;
            int rookCol = rookIdx % BoardWidth;
            bool openFile = true;
            for (int r = 0; r < BoardHeight; r++)
            {
                var p = board.GetPiece(r * BoardWidth + rookCol);
                if (p.Type == PieceType.Pawn && p.Color == color)
                {
                    openFile = false;
                    break;
                }
            }
            if (openFile) bonus += 10;
        }

        return bonus;
    }
}
