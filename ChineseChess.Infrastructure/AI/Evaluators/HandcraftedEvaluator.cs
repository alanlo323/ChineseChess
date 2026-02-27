using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System.Linq;

namespace ChineseChess.Infrastructure.AI.Evaluators;

public class HandcraftedEvaluator : IEvaluator
{
    private const int BoardWidth = 9;

    // Basic material values
    private static readonly int[] PieceValues = new int[]
    {
        0,      // None
        10000,  // King
        120,    // Advisor
        120,    // Elephant
        270,    // Horse
        600,    // Rook
        285,    // Cannon
        30      // Pawn (avg)
    };

    public int Evaluate(IBoard board)
    {
        int score = 0;
        int mobilityBonus = board.GenerateLegalMoves().Count() * 2;
        
        // Material & PST
        for (int i = 0; i < 90; i++)
        {
            var p = board.GetPiece(i);
            if (p.IsNone) continue;

            int val = PieceValues[(int)p.Type];
            
            // Simple PST bonus (center control, river crossing) - placeholder
            // if (p.Type == PieceType.Pawn && crossedRiver) val += 30;

            if (p.Color == PieceColor.Red) score += val;
            else score -= val;

            int row = i / BoardWidth;
            int col = i % BoardWidth;

            // Pawn development bonus: pawn crossing river and more advanced pieces
            if (p.Type == PieceType.Pawn)
            {
                if (p.Color == PieceColor.Red)
                {
                    if (row <= 4) score += 12; // already crossed river
                    score += Math.Max(0, 4 - row); // keep advancing toward home side
                }
                else
                {
                    if (row >= 5) score += 12;
                    score += Math.Max(0, row - 5);
                }
            }

            // Light center control bonus for active pieces
            if (col == 4)
            {
                if (p.Type == PieceType.Horse || p.Type == PieceType.Rook || p.Type == PieceType.Cannon)
                {
                    if (p.Color == PieceColor.Red) score += 4;
                    else score -= 4;
                }
            }
        }

        // Current side mobility bonus (positive for side-to-move)
        if (board.Turn == PieceColor.Red) score += mobilityBonus;
        else score -= mobilityBonus;

        // Perspective: positive for current turn
        return board.Turn == PieceColor.Red ? score : -score;
    }
}
