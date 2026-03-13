using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.AI.Evaluators;

/// <summary>
/// 空間控制評估模組（L3）。
/// 統計己方棋子能攻擊到的敵方半場格數，給予微幅加成。
///
/// 評估方式：
///   - 對每個己方攻擊型棋子（車、馬、炮），計算其可攻擊到的「敵方半場」格子數
///   - 紅方敵方半場：row 0-4；黑方敵方半場：row 5-9
///   - 每個可控制的格子給予微小加成（避免評估值過大）
///
/// 注意：此模組採用輕量計算，不調用完整 GenerateLegalMoves。
/// </summary>
public static class SpaceControl
{
    private const int BoardWidth = 9;
    private const int BoardHeight = 10;

    // 每個可控制的敵方半場格子的加成分值
    private const int SquareControlBonus = 1;

    // 車的掃描方向
    private static readonly int[] RookDr = { -1, +1,  0,  0 };
    private static readonly int[] RookDc = {  0,  0, -1, +1 };

    // 馬的跳躍向量（腳位方向 + 目標偏移）
    private static readonly (int LegDr, int LegDc, int TgtDr, int TgtDc)[] HorseMoves =
    {
        (-1,  0, -2, -1), (-1,  0, -2, +1),
        (+1,  0, +2, -1), (+1,  0, +2, +1),
        ( 0, -1, -1, -2), ( 0, -1, +1, -2),
        ( 0, +1, -1, +2), ( 0, +1, +1, +2),
    };

    /// <summary>
    /// 計算指定顏色方的空間控制分數。
    /// 統計所有攻擊型棋子（車、馬、炮）能到達敵方半場的格子數。
    /// </summary>
    public static int Calculate(IBoard board, PieceColor color)
    {
        int controlledSquares = 0;

        // 敵方半場的行範圍
        int enemyRowStart = color == PieceColor.Red ? 0 : 5;
        int enemyRowEnd   = color == PieceColor.Red ? 5 : 10;

        for (int i = 0; i < 90; i++)
        {
            var piece = board.GetPiece(i);
            if (piece.IsNone || piece.Color != color) continue;

            switch (piece.Type)
            {
                case PieceType.Rook:
                    controlledSquares += CountRookControlledSquares(board, i, enemyRowStart, enemyRowEnd);
                    break;
                case PieceType.Horse:
                    controlledSquares += CountHorseControlledSquares(board, i, enemyRowStart, enemyRowEnd);
                    break;
                case PieceType.Cannon:
                    controlledSquares += CountCannonControlledSquares(board, i, enemyRowStart, enemyRowEnd);
                    break;
            }
        }

        return controlledSquares * SquareControlBonus;
    }

    /// <summary>
    /// 計算車在敵方半場可到達的格子數（移動或吃子）。
    /// </summary>
    private static int CountRookControlledSquares(IBoard board, int rookIndex, int rowStart, int rowEnd)
    {
        int r = rookIndex / BoardWidth;
        int c = rookIndex % BoardWidth;
        int count = 0;

        for (int dir = 0; dir < 4; dir++)
        {
            int nr = r + RookDr[dir];
            int nc = c + RookDc[dir];

            while (nr >= 0 && nr < BoardHeight && nc >= 0 && nc < BoardWidth)
            {
                if (nr >= rowStart && nr < rowEnd) count++;
                var piece = board.GetPiece(nr * BoardWidth + nc);
                if (!piece.IsNone) break;  // 遇到棋子停止
                nr += RookDr[dir];
                nc += RookDc[dir];
            }
        }

        return count;
    }

    /// <summary>
    /// 計算馬在敵方半場可到達的格子數。
    /// </summary>
    private static int CountHorseControlledSquares(IBoard board, int horseIndex, int rowStart, int rowEnd)
    {
        int r = horseIndex / BoardWidth;
        int c = horseIndex % BoardWidth;
        int count = 0;

        foreach (var (legDr, legDc, tgtDr, tgtDc) in HorseMoves)
        {
            int legR = r + legDr;
            int legC = c + legDc;

            if (legR < 0 || legR >= BoardHeight || legC < 0 || legC >= BoardWidth) continue;
            if (!board.GetPiece(legR * BoardWidth + legC).IsNone) continue;

            int tgtR = r + tgtDr;
            int tgtC = c + tgtDc;

            if (tgtR < 0 || tgtR >= BoardHeight || tgtC < 0 || tgtC >= BoardWidth) continue;
            if (tgtR >= rowStart && tgtR < rowEnd) count++;
        }

        return count;
    }

    /// <summary>
    /// 計算炮在敵方半場可到達的格子數（移動或跳吃）。
    /// </summary>
    private static int CountCannonControlledSquares(IBoard board, int cannonIndex, int rowStart, int rowEnd)
    {
        int r = cannonIndex / BoardWidth;
        int c = cannonIndex % BoardWidth;
        int count = 0;

        for (int dir = 0; dir < 4; dir++)
        {
            int nr = r + RookDr[dir];
            int nc = c + RookDc[dir];
            bool foundScreen = false;

            while (nr >= 0 && nr < BoardHeight && nc >= 0 && nc < BoardWidth)
            {
                var piece = board.GetPiece(nr * BoardWidth + nc);

                if (piece.IsNone)
                {
                    if (!foundScreen && nr >= rowStart && nr < rowEnd) count++;  // 可移動到的敵方格
                }
                else
                {
                    if (!foundScreen)
                    {
                        foundScreen = true;
                    }
                    else
                    {
                        if (nr >= rowStart && nr < rowEnd) count++;  // 可吃的敵方格
                        break;
                    }
                }

                nr += RookDr[dir];
                nc += RookDc[dir];
            }
        }

        return count;
    }
}
