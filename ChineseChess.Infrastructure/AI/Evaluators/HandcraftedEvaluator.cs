using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.AI.Evaluators;

public class HandcraftedEvaluator : IEvaluator
{
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
        }

        // Perspective: positive for current turn
        return board.Turn == PieceColor.Red ? score : -score;
    }
}
