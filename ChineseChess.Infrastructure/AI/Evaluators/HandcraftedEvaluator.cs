using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System;
namespace ChineseChess.Infrastructure.AI.Evaluators;

public class HandcraftedEvaluator : IEvaluator
{
    private const int BoardSize = 90;
    private const int BoardWidth = 9;
    private const int BoardHeight = 10;

    // 馬腳封堵懲罰：每個被封堵的腳位扣除的分數
    private const int HorseLegBlockedPenalty = 10;

    // 炮威脅加分：炮透過炮台瞄準對方棋子的加分
    private const int CannonKingThreatBonus  = 40; // 直接打將/帥
    private const int CannonPieceThreatBonus = 10; // 打其他棋子

    // 敵車壓制懲罰：敵方車在帥/將列上直接瞄準
    private const int EnemyRookOpenColumnPenalty     = 60; // 無阻隔（直接壓制）
    private const int EnemyRookSemiOpenColumnPenalty = 20; // 一子阻隔（半開放）

    private static readonly int[] PieceValues =
    {
        0,      // None
        10000,  // King
        120,    // Advisor
        120,    // Elephant
        270,    // Horse
        600,    // Rook
        285,    // Cannon
        30      // Pawn（基礎分，PST 再加位置加成）
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

        // 材料分
            score += sign * PieceValues[(int)p.Type];

        // PST（位置分）
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
                case PieceType.Horse:
                    // 馬腳封堵懲罰：每個被佔據的腳位扣除固定分數
                    score -= sign * CountHorseLegsBlocked(board, i) * HorseLegBlockedPenalty;
                    break;
                case PieceType.Cannon:
                    // 炮威脅加分：炮透過炮台瞄準對方棋子
                    score += sign * EvaluateCannonThreats(board, i, p.Color);
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

        // --- 王將安全性 ---
        score += EvaluateKingSafety(board, PieceColor.Red, redKingIndex, redAdvisors, redElephants);
        score -= EvaluateKingSafety(board, PieceColor.Black, blackKingIndex, blackAdvisors, blackElephants);

        // --- 敵車對帥/將的縱列壓制 ---
        score += EvaluateEnemyRookPressure(board, PieceColor.Red, redKingIndex);
        score -= EvaluateEnemyRookPressure(board, PieceColor.Black, blackKingIndex);

        // --- 棋子結構 ---
        score += EvaluateRookStructure(board, PieceColor.Red, redRook1, redRook2, redRookCount);
        score -= EvaluateRookStructure(board, PieceColor.Black, blackRook1, blackRook2, blackRookCount);

        // --- 機動力（輕量：以局面素材估算可行性） ---
        int mobility = EstimatePotentialMobility(board);
        score += (board.Turn == PieceColor.Red ? 1 : -1) * mobility;

        // 以輪到行動的一方為觀點回傳分數
        return board.Turn == PieceColor.Red ? score : -score;
    }

    /// <summary>
    /// 計算馬在 <paramref name="horseIndex"/> 位置被封堵的腳位數量。
    /// 馬的四個腳位為：上(r-1,c)、下(r+1,c)、左(r,c-1)、右(r,c+1)。
    /// 任何棋子（友方或敵方）佔據腳位均視為封堵。
    /// </summary>
    private static int CountHorseLegsBlocked(IBoard board, int horseIndex)
    {
        int r = horseIndex / BoardWidth;
        int c = horseIndex % BoardWidth;
        int blocked = 0;

        if (r > 0              && !board.GetPiece((r - 1) * BoardWidth + c).IsNone) blocked++;
        if (r < BoardHeight - 1 && !board.GetPiece((r + 1) * BoardWidth + c).IsNone) blocked++;
        if (c > 0              && !board.GetPiece(r * BoardWidth + (c - 1)).IsNone) blocked++;
        if (c < BoardWidth - 1  && !board.GetPiece(r * BoardWidth + (c + 1)).IsNone) blocked++;

        return blocked;
    }

    /// <summary>
    /// 計算炮在 <paramref name="cannonIndex"/> 位置的威脅加分。
    /// 掃描四個方向：找到第一個棋子（炮台）後，若其後方有敵子（目標），則給予加分。
    /// 打將/帥的加分高於打普通棋子。
    /// </summary>
    private static int EvaluateCannonThreats(IBoard board, int cannonIndex, PieceColor cannonColor)
    {
        int bonus = 0;
        int r = cannonIndex / BoardWidth;
        int c = cannonIndex % BoardWidth;

        int[] dr = { -1, +1, 0, 0 };
        int[] dc = { 0, 0, -1, +1 };

        for (int dir = 0; dir < 4; dir++)
        {
            bool foundScreen = false;
            int nr = r + dr[dir], nc = c + dc[dir];

            while (nr >= 0 && nr < BoardHeight && nc >= 0 && nc < BoardWidth)
            {
                var piece = board.GetPiece(nr * BoardWidth + nc);

                if (!piece.IsNone)
                {
                    if (!foundScreen)
                    {
                        foundScreen = true; // 找到炮台，繼續掃描找目標
                    }
                    else
                    {
                        // 找到炮台後的第一個棋子
                        if (piece.Color != cannonColor)
                        {
                            // 敵方棋子：按類型給予威脅加分
                            bonus += piece.Type == PieceType.King
                                ? CannonKingThreatBonus
                                : CannonPieceThreatBonus;
                        }
                        break; // 每條射線只算一個目標
                    }
                }

                nr += dr[dir];
                nc += dc[dir];
            }
        }

        return bonus;
    }

    private static int EstimatePotentialMobility(IBoard board)
    {
        int redPotential = 0;
        int blackPotential = 0;

        for (int i = 0; i < BoardSize; i++)
        {
            var piece = board.GetPiece(i);
            if (piece.IsNone) continue;

            int value = piece.Type switch
            {
                PieceType.King => 4,
                PieceType.Advisor => 4,
                PieceType.Elephant => 4,
                PieceType.Horse => 6,
                PieceType.Rook => 12,
                PieceType.Cannon => 10,
                PieceType.Pawn => 3,
                _ => 0
            };

            if (piece.Color == PieceColor.Red)
                redPotential += value;
            else
                blackPotential += value;
        }

        return redPotential - blackPotential;
    }

    /// <summary>
    /// 評估敵方車在帥/將縱列上的直接壓制威脅。
    /// 掃描帥/將朝敵方方向的同列，遇到敵車時依中間子數給予懲罰：
    ///   0 子阻隔 = 直接壓制（-<see cref="EnemyRookOpenColumnPenalty"/>）；
    ///   1 子阻隔 = 半開放（-<see cref="EnemyRookSemiOpenColumnPenalty"/>）。
    /// </summary>
    private static int EvaluateEnemyRookPressure(IBoard board, PieceColor kingColor, int kingIndex)
    {
        if (kingIndex < 0) return 0;

        int kingRow = kingIndex / BoardWidth;
        int kingCol = kingIndex % BoardWidth;
        int direction = kingColor == PieceColor.Red ? -1 : 1;
        int screenCount = 0;

        for (int r = kingRow + direction; r >= 0 && r < BoardHeight; r += direction)
        {
            var p = board.GetPiece(r * BoardWidth + kingCol);
            if (p.IsNone) continue;

            if (p.Color != kingColor && p.Type == PieceType.Rook)
            {
                return screenCount == 0
                    ? -EnemyRookOpenColumnPenalty
                    : -EnemyRookSemiOpenColumnPenalty;
            }

            screenCount++;
            if (screenCount >= 2) break; // 兩子後即便有車也威脅不大
        }

        return 0;
    }

    private static int EvaluateKingSafety(IBoard board, PieceColor color, int kingIndex,
        int advisorCount, int elephantCount)
    {
        if (kingIndex < 0) return 0;

        int bonus = 0;

        // 少了宮廷防守棋子的懲罰
        if (advisorCount < 2) bonus -= (2 - advisorCount) * 20;
        if (elephantCount < 2) bonus -= (2 - elephantCount) * 10;

        // 曝露 king 懲罰：檢查皇帝/將所在直列是否對敵方敞開
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

        // 雙車連線加分（同排或同列）
        if (rookCount == 2 && rook1 >= 0 && rook2 >= 0)
        {
            int r1Row = rook1 / BoardWidth, r1Col = rook1 % BoardWidth;
            int r2Row = rook2 / BoardWidth, r2Col = rook2 % BoardWidth;
            if (r1Row == r2Row || r1Col == r2Col) bonus += 15;
        }

        // 車站在空列上的加分（同一欄位無己方棋子）
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
