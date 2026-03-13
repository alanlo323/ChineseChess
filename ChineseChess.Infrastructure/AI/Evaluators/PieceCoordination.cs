using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.AI.Evaluators;

/// <summary>
/// 棋子協同評估模組（L2）。
/// 評估以下兩種協同形態：
///   1. 馬炮同列/同排配合：馬和炮在同一橫排或縱列時，形成互相支援的攻勢 +12
///   2. 車炮同列（縱列）威脅：車和炮在同一縱列時，炮可依托車為炮台攻擊 +15
///
/// 以指定顏色方的視角回傳分數（正值=有利）。
/// </summary>
public static class PieceCoordination
{
    private const int BoardWidth = 9;
    private const int BoardHeight = 10;

    // 馬炮協同加分：同排或同列
    private const int HorseCannonBonus = 12;

    // 車炮同列加分：車炮在同一縱列形成雙重威脅
    private const int RookCannonColumnBonus = 15;

    /// <summary>
    /// 計算指定顏色方的棋子協同分數。
    /// </summary>
    public static int Evaluate(IBoard board, PieceColor color)
    {
        int score = 0;

        // 收集所有友方馬、車、炮的位置
        var horsePositions = new System.Collections.Generic.List<int>();
        var rookPositions = new System.Collections.Generic.List<int>();
        var cannonPositions = new System.Collections.Generic.List<int>();

        for (int i = 0; i < 90; i++)
        {
            var piece = board.GetPiece(i);
            if (piece.IsNone || piece.Color != color) continue;

            switch (piece.Type)
            {
                case PieceType.Horse:  horsePositions.Add(i); break;
                case PieceType.Rook:   rookPositions.Add(i);  break;
                case PieceType.Cannon: cannonPositions.Add(i); break;
            }
        }

        // 1. 馬炮同列/同排協同
        foreach (int horseIdx in horsePositions)
        {
            int horseRow = horseIdx / BoardWidth;
            int horseCol = horseIdx % BoardWidth;

            foreach (int cannonIdx in cannonPositions)
            {
                int cannonRow = cannonIdx / BoardWidth;
                int cannonCol = cannonIdx % BoardWidth;

                if (horseRow == cannonRow || horseCol == cannonCol)
                {
                    score += HorseCannonBonus;
                }
            }
        }

        // 2. 車炮同列（縱列）威脅加分
        foreach (int rookIdx in rookPositions)
        {
            int rookCol = rookIdx % BoardWidth;

            foreach (int cannonIdx in cannonPositions)
            {
                int cannonCol = cannonIdx % BoardWidth;

                if (rookCol == cannonCol)
                {
                    score += RookCannonColumnBonus;
                }
            }
        }

        return score;
    }
}
