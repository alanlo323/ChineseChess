using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.AI.Evaluators;

/// <summary>
/// 棋子活動力輕量計算工具類別（M2）。
/// 不依賴完整的 GenerateLegalMoves，採用輕量方式直接計算可攻擊格數：
///   - 車：掃描四個方向，計算到第一個阻擋棋子（含）之前的格子數
///   - 馬：枚舉八個跳躍目標，檢查腳位是否被封堵
///   - 炮：掃描四個方向，計算到第一個棋子之前的格子數（炮台前的空格）
/// 效能：O(board_size) 而非 O(moves_count * board_size)。
/// </summary>
public static class MobilityEvaluator
{
    private const int BoardWidth = 9;
    private const int BoardHeight = 10;

    // 車的掃描方向：上下左右
    private static readonly int[] RookDr = { -1, +1,  0,  0 };
    private static readonly int[] RookDc = {  0,  0, -1, +1 };

    // 馬的跳躍向量：(腳位dr, 腳位dc, 目標dr, 目標dc)
    private static readonly (int LegDr, int LegDc, int TgtDr, int TgtDc)[] HorseMoves =
    {
        (-1,  0, -2, -1), (-1,  0, -2, +1),  // 向上跳
        (+1,  0, +2, -1), (+1,  0, +2, +1),  // 向下跳
        ( 0, -1, -1, -2), ( 0, -1, +1, -2),  // 向左跳
        ( 0, +1, -1, +2), ( 0, +1, +1, +2),  // 向右跳
    };

    /// <summary>
    /// 計算車在指定位置的活動格數（輕量版）。
    /// 掃描四個方向，每方向計算到第一個棋子之前的空格數，不含棋子本身（被己方擋）或含（吃子）。
    /// 此處為簡化版：計算所有可達的空格＋可吃格。
    /// </summary>
    public static int CalculateRookMobility(IBoard board, int rookIndex)
    {
        int r = rookIndex / BoardWidth;
        int c = rookIndex % BoardWidth;
        int mobility = 0;

        for (int dir = 0; dir < 4; dir++)
        {
            int nr = r + RookDr[dir];
            int nc = c + RookDc[dir];

            while (nr >= 0 && nr < BoardHeight && nc >= 0 && nc < BoardWidth)
            {
                var piece = board.GetPiece(nr * BoardWidth + nc);
                mobility++;  // 此格可達（空格或可吃）
                if (!piece.IsNone) break;  // 遇到棋子停止
                nr += RookDr[dir];
                nc += RookDc[dir];
            }
        }

        return mobility;
    }

    /// <summary>
    /// 計算馬在指定位置的活動格數（輕量版）。
    /// 枚舉八個跳躍目標，檢查：1) 腳位不被封堵；2) 目標格在棋盤內。
    /// </summary>
    public static int CalculateHorseMobility(IBoard board, int horseIndex)
    {
        int r = horseIndex / BoardWidth;
        int c = horseIndex % BoardWidth;
        int mobility = 0;

        foreach (var (legDr, legDc, tgtDr, tgtDc) in HorseMoves)
        {
            int legR = r + legDr;
            int legC = c + legDc;

            // 腳位必須在棋盤內且不被佔據
            if (legR < 0 || legR >= BoardHeight || legC < 0 || legC >= BoardWidth) continue;
            if (!board.GetPiece(legR * BoardWidth + legC).IsNone) continue;

            // 目標格必須在棋盤內
            int tgtR = r + tgtDr;
            int tgtC = c + tgtDc;
            if (tgtR < 0 || tgtR >= BoardHeight || tgtC < 0 || tgtC >= BoardWidth) continue;

            mobility++;
        }

        return mobility;
    }

    /// <summary>
    /// 計算炮在指定位置的活動格數（輕量版）。
    /// 掃描四個方向，計算到第一個棋子之前的空格數（炮只能走不吃，跳過炮台才能吃）。
    /// 此處計算：空格數（可移動）+ 越過炮台後的敵方棋子數（可吃）。
    /// </summary>
    public static int CalculateCannonMobility(IBoard board, int cannonIndex)
    {
        int r = cannonIndex / BoardWidth;
        int c = cannonIndex % BoardWidth;
        int mobility = 0;

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
                    if (!foundScreen) mobility++;  // 炮台前的空格：可移動
                }
                else
                {
                    if (!foundScreen)
                    {
                        foundScreen = true;  // 找到炮台
                    }
                    else
                    {
                        mobility++;  // 炮台後第一個棋子：可吃
                        break;
                    }
                }

                nr += RookDr[dir];
                nc += RookDc[dir];
            }
        }

        return mobility;
    }

    /// <summary>
    /// 計算所有棋子的總活動力差值（紅方 - 黑方）。
    /// 用於在 HandcraftedEvaluator 中替換 EstimatePotentialMobility。
    /// </summary>
    public static int CalculateTotalMobility(IBoard board)
    {
        int redMobility = 0;
        int blackMobility = 0;

        for (int i = 0; i < 90; i++)
        {
            var piece = board.GetPiece(i);
            if (piece.IsNone) continue;

            int mob = piece.Type switch
            {
                PieceType.Rook   => CalculateRookMobility(board, i),
                PieceType.Horse  => CalculateHorseMobility(board, i),
                PieceType.Cannon => CalculateCannonMobility(board, i),
                // 其他棋子使用固定估算值（輕量）
                PieceType.King     => 4,
                PieceType.Advisor  => 4,
                PieceType.Elephant => 4,
                PieceType.Pawn     => 3,
                _ => 0
            };

            if (piece.Color == PieceColor.Red)
                redMobility += mob;
            else
                blackMobility += mob;
        }

        return redMobility - blackMobility;
    }
}
