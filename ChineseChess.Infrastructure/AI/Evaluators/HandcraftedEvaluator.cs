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

    // 被困棋子懲罰（Trapped Piece Penalty）：
    //   馬的所有腳位都被封堵（0 mobility）時的額外懲罰。
    //   與腳位封堵懲罰（HorseLegBlockedPenalty）堆疊計算：
    //     完全被困 = 4×10（腳封） + 50（被困） = 90 cp 總懲罰。
    private const int TrappedHorsePenalty = 50;

    // 馬前哨陣地加分（C1）：跨河且機動力 ≥ 2 的馬，反映進攻威脅
    private const int HorseOutpostBonus = 15;

    // 車入底線加分（C2）：車深入對方底部兩排，反映終殺威脅
    private const int RookPenetrationBonus = 12;

    // 殘局帥/將趨中宮加分（B1）
    private const int KingCentralityBonusPerStep   = 4;  // 每靠近中心一格的加分
    private const int KingCentralityMaxDist        = 4;  // 超過此距離不加分
    // 相位範圍 0–256（256=完整開局材料，0=僅剩帥將）。80/256 ≈ 31% 為中後盤啟動門檻
    private const int KingCentralityPhaseThreshold = 80;

    // 雙象雙士完整防守加分（plan C1）
    private const int FullDefenseBonus    = 20; // 雙象+雙士俱在
    private const int DoubleAdvisorBonus  = 8;  // 僅雙士
    private const int DoubleElephantBonus = 8;  // 僅雙象

    // 炮台品質加分（plan C2）
    private const int CannonScreenStrongBonus = 10; // 友方象/士作炮台（難被拆除）
    private const int CannonScreenWeakBonus   = 5;  // 友方馬/炮作炮台

    // 炮威脅加分：炮透過炮台瞄準對方棋子的加分
    private const int CannonKingThreatBonus  = 40; // 直接打將/帥
    private const int CannonPieceThreatBonus = 10; // 打其他棋子

    // 敵車壓制懲罰：敵方車在帥/將列上直接瞄準
    private const int EnemyRookOpenColumnPenalty     = 60; // 無阻隔（直接壓制）
    private const int EnemyRookSemiOpenColumnPenalty = 20; // 一子阻隔（半開放）

    // 殘局棋子價值調整（Tapered Evaluation）：
    //   炮在殘局因缺乏炮台而威力下降；馬/象/士在殘局防守和機動更重要
    private const int EndgameCannonAdjust   = 20; // 殘局炮 -20
    private const int EndgameHorseAdjust    = 20; // 殘局馬 +20
    private const int EndgameElephantAdjust = 15; // 殘局象 +15
    private const int EndgameAdvisorAdjust  = 15; // 殘局士 +15

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
        // H3：計算棋局相位，用於對位置分進行插值
        int phase = GamePhase.Calculate(board);

        int score = 0;

        int redKingIndex = -1, blackKingIndex = -1;
        int redAdvisors = 0, blackAdvisors = 0;
        int redElephants = 0, blackElephants = 0;
        int redRookCount = 0, blackRookCount = 0;
        int redRook1 = -1, redRook2 = -1;
        int blackRook1 = -1, blackRook2 = -1;
        // 機動力累加器：整合至主迴圈，避免二次全棋盤掃描
        int redMobility = 0, blackMobility = 0;

        for (int i = 0; i < BoardSize; i++)
        {
            var p = board.GetPiece(i);
            if (p.IsNone) continue;

            int sign = p.Color == PieceColor.Red ? 1 : -1;

            // 材料分
            score += sign * PieceValues[(int)p.Type];

            // 殘局棋子價值調整（Tapered）：開局無調整，殘局漸增
            int endgameAdj = GetEndgameAdjustment(p.Type);
            if (endgameAdj != 0)
                score += sign * GamePhase.Interpolate(0, endgameAdj, phase);

            // PST（位置分）：根據棋局相位插值
            int pstFull = PieceSquareTables.GetScore(p.Type, p.Color, i);
            int pstHalf = pstFull / 2;
            int pstValue = GamePhase.Interpolate(pstFull, pstHalf, phase);
            score += sign * pstValue;

            switch (p.Type)
            {
                case PieceType.King:
                    if (p.Color == PieceColor.Red) { redKingIndex = i; redMobility += 4; }
                    else { blackKingIndex = i; blackMobility += 4; }
                    break;
                case PieceType.Advisor:
                    if (p.Color == PieceColor.Red) { redAdvisors++; redMobility += 4; }
                    else { blackAdvisors++; blackMobility += 4; }
                    break;
                case PieceType.Elephant:
                    if (p.Color == PieceColor.Red) { redElephants++; redMobility += 4; }
                    else { blackElephants++; blackMobility += 4; }
                    break;
                case PieceType.Horse:
                {
                    // 合併計算：活動格數 + 被封堵腳位數（單次掃描，減少 67% 棋盤存取）
                    int horseMob = MobilityEvaluator.CalculateHorseMobility(board, i, out int horseLegsBlocked);
                    // 被困馬懲罰：所有腳位封堵導致 0 mobility 時的一次性額外懲罰
                    if (horseMob == 0)
                        score -= sign * TrappedHorsePenalty;
                    // 馬腳封堵懲罰：每個被佔據的腳位扣除固定分數
                    score -= sign * horseLegsBlocked * HorseLegBlockedPenalty;
                    // 前哨加分（C1）：跨河且機動力充足的馬
                    if (IsHorseOutpost(i, p.Color, horseMob))
                        score += sign * HorseOutpostBonus;
                    if (p.Color == PieceColor.Red) redMobility += horseMob;
                    else blackMobility += horseMob;
                    break;
                }
                case PieceType.Cannon:
                {
                    // 炮威脅加分：炮透過炮台瞄準對方棋子
                    score += sign * EvaluateCannonThreats(board, i, p.Color);
                    int cannonMob = MobilityEvaluator.CalculateCannonMobility(board, i);
                    if (p.Color == PieceColor.Red) redMobility += cannonMob;
                    else blackMobility += cannonMob;
                    break;
                }
                case PieceType.Rook:
                {
                    int rookMob = MobilityEvaluator.CalculateRookMobility(board, i);
                    // 入底線加分（C2）：車深入對方底部兩排
                    score += sign * EvaluateRookPenetration(i, p.Color);
                    if (p.Color == PieceColor.Red)
                    {
                        if (redRookCount == 0) redRook1 = i; else redRook2 = i;
                        redRookCount++;
                        redMobility += rookMob;
                    }
                    else
                    {
                        if (blackRookCount == 0) blackRook1 = i; else blackRook2 = i;
                        blackRookCount++;
                        blackMobility += rookMob;
                    }
                    break;
                }
                default: // Pawn（固定估算值）
                    if (p.Color == PieceColor.Red) redMobility += 3;
                    else blackMobility += 3;
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

        // --- 機動力（已在主迴圈計算，避免二次全棋盤掃描） ---
        // 車/馬/炮：實算可達格數；帥/將/士/象/兵：沿用固定估算值
        // B2：相位加權機動力，殘局時機動力更重要（×1.5）
        // mobility 已是紅-黑差值（正=紅方優勢），最終 return 時依 Turn 翻轉觀點
        int mobility = redMobility - blackMobility;
        int mobilityWeight = GamePhase.Interpolate(10, 15, phase);
        score += mobility * mobilityWeight / 10;

        // --- 殘局帥/將趨中宮加分（B1）---
        score += EvaluateKingCentrality(redKingIndex, PieceColor.Red, phase);
        score -= EvaluateKingCentrality(blackKingIndex, PieceColor.Black, phase);

        // --- 雙象雙士完整防守加分（plan C1）---
        score += EvaluateDefenseFormation(redAdvisors, redElephants);
        score -= EvaluateDefenseFormation(blackAdvisors, blackElephants);

        // --- 兵型結構（M3）---
        score += PawnStructure.Evaluate(board, PieceColor.Red);
        score -= PawnStructure.Evaluate(board, PieceColor.Black);

        // --- 棋子協同（L2）---
        score += PieceCoordination.Evaluate(board, PieceColor.Red);
        score -= PieceCoordination.Evaluate(board, PieceColor.Black);

        // --- 空間控制（L3）---
        score += SpaceControl.Calculate(board, PieceColor.Red);
        score -= SpaceControl.Calculate(board, PieceColor.Black);

        // 以輪到行動的一方為觀點回傳分數
        return board.Turn == PieceColor.Red ? score : -score;
    }

    /// <summary>
    /// 快速評估：只含 Material + PST（含 GamePhase 插值）。
    /// 跳過王安全、機動力、兵型、炮威脅、馬腳封堵、車壓制、棋子協同、空間控制。
    /// </summary>
    public int EvaluateFast(IBoard board)
    {
        int phase = GamePhase.Calculate(board);
        int score = 0;

        for (int i = 0; i < BoardSize; i++)
        {
            var p = board.GetPiece(i);
            if (p.IsNone) continue;

            int sign = p.Color == PieceColor.Red ? 1 : -1;

            // 材料分
            score += sign * PieceValues[(int)p.Type];

            // 殘局棋子價值調整（與 Evaluate() 共用 GetEndgameAdjustment）
            int endgameAdjFast = GetEndgameAdjustment(p.Type);
            if (endgameAdjFast != 0)
                score += sign * GamePhase.Interpolate(0, endgameAdjFast, phase);

            // PST（位置分）：根據棋局相位插值
            int pstFull = PieceSquareTables.GetScore(p.Type, p.Color, i);
            int pstHalf = pstFull / 2;
            int pstValue = GamePhase.Interpolate(pstFull, pstHalf, phase);
            score += sign * pstValue;
        }

        // 以輪到行動的一方為觀點回傳分數
        return board.Turn == PieceColor.Red ? score : -score;
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
                        // 炮台品質加分（plan C2）：友方穩固棋子作炮台更優
                        if (piece.Color == cannonColor)
                        {
                            bonus += piece.Type is PieceType.Elephant or PieceType.Advisor
                                ? CannonScreenStrongBonus
                                : piece.Type is PieceType.Horse or PieceType.Cannon
                                    ? CannonScreenWeakBonus
                                    : 0;
                        }
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

    /// <summary>
    /// 殘局棋子價值調整量（Evaluate/EvaluateFast 共用）。
    /// 殘局時炮威力下降（-20），馬/象/士防守更重要（+20/+15/+15）。
    /// </summary>
    private static int GetEndgameAdjustment(PieceType type) => type switch
    {
        PieceType.Cannon   => -EndgameCannonAdjust,
        PieceType.Horse    => +EndgameHorseAdjust,
        PieceType.Elephant => +EndgameElephantAdjust,
        PieceType.Advisor  => +EndgameAdvisorAdjust,
        _ => 0
    };

    /// <summary>
    /// 雙象雙士完整防守加分（plan C1）。
    /// 雙象+雙士俱在 → +20；僅雙士或僅雙象 → +8。
    /// </summary>
    private static int EvaluateDefenseFormation(int advisorCount, int elephantCount)
    {
        bool doubleAdvisor = advisorCount >= 2;
        bool doubleElephant = elephantCount >= 2;

        if (doubleAdvisor && doubleElephant) return FullDefenseBonus;
        if (doubleAdvisor) return DoubleAdvisorBonus;
        if (doubleElephant) return DoubleElephantBonus;
        return 0;
    }

    /// <summary>
    /// 殘局帥/將趨中宮加分（B1）。
    /// 相位低於 <see cref="KingCentralityPhaseThreshold"/> 時，
    /// 帥/將距本宮中心（紅:row8,col4; 黑:row1,col4）越近加分越多。
    /// </summary>
    private static int EvaluateKingCentrality(int kingIndex, PieceColor color, int phase)
    {
        if (kingIndex < 0 || phase >= KingCentralityPhaseThreshold) return 0;
        int centerRow = color == PieceColor.Red ? 8 : 1;
        int r = kingIndex / BoardWidth;
        int c = kingIndex % BoardWidth;
        int dist = Math.Abs(r - centerRow) + Math.Abs(c - 4);
        int rawBonus = Math.Max(0, KingCentralityMaxDist - dist) * KingCentralityBonusPerStep;
        return rawBonus * (KingCentralityPhaseThreshold - phase) / KingCentralityPhaseThreshold;
    }

    /// <summary>
    /// 判斷馬是否位於前哨陣地（C1）：跨過河界且機動力 ≥ 2。
    /// 紅方跨河條件：row &lt; 5；黑方：row &gt; 4。
    /// </summary>
    private static bool IsHorseOutpost(int horseIndex, PieceColor color, int mobility)
    {
        if (mobility < 2) return false;
        int row = horseIndex / BoardWidth;
        return color == PieceColor.Red ? row < 5 : row > 4;
    }

    /// <summary>
    /// 計算車入底線加分（C2）：車深入對方底部兩排時給予加分。
    /// 紅方條件：row ≤ 1；黑方：row ≥ 8。
    /// </summary>
    private static int EvaluateRookPenetration(int rookIndex, PieceColor color)
    {
        int row = rookIndex / BoardWidth;
        bool penetrating = color == PieceColor.Red ? row <= 1 : row >= 8;
        return penetrating ? RookPenetrationBonus : 0;
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
