using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.AI.Evaluators;

/// <summary>
/// 兵型結構評估模組（M3）。
/// 評估以下三種兵型形態：
///   1. 連兵加成（Connected Pawn Bonus）：相鄰列且同排兵互相保護 +8
///   2. 孤兵懲罰（Isolated Pawn Penalty）：左右列均無友兵 -5
///   3. 過河兵協同加成（Crossed Pawn Coordination）：過河兵 >= 2 時每兵 +10
///
/// 以指定顏色方的視角回傳分數（正值=有利）。
/// </summary>
public static class PawnStructure
{
    private const int BoardWidth = 9;
    private const int BoardHeight = 10;

    // 連兵加成：兩兵在相鄰列且在相鄰排（互相保護）
    private const int ConnectedPawnBonus = 8;

    // 孤兵懲罰：兵的左右列均無友兵
    private const int IsolatedPawnPenalty = 5;

    // 過河兵協同加成：過河兵 >= 2 時每個過河兵額外加分
    private const int CrossedPawnCoordinationBonus = 10;

    // 疊兵懲罰：同列兵超過 1 個時，每多一個的懲罰分
    private const int DoubledPawnPenalty = 8;

    /// <summary>
    /// 計算指定顏色方的兵型結構分數。
    /// </summary>
    public static int Evaluate(IBoard board, PieceColor color)
    {
        int score = 0;

        // 收集所有友兵位置
        var pawnPositions = new System.Collections.Generic.List<int>();
        for (int i = 0; i < 90; i++)
        {
            var piece = board.GetPiece(i);
            if (!piece.IsNone && piece.Type == PieceType.Pawn && piece.Color == color)
            {
                pawnPositions.Add(i);
            }
        }

        if (pawnPositions.Count == 0) return 0;

        // 疊兵懲罰：統計每列兵數，超過 1 個時每多一個扣 DoubledPawnPenalty 分
        var pawnsByColumn = new int[BoardWidth];
        foreach (int pos in pawnPositions)
            pawnsByColumn[pos % BoardWidth]++;
        for (int col = 0; col < BoardWidth; col++)
            if (pawnsByColumn[col] > 1)
                score -= (pawnsByColumn[col] - 1) * DoubledPawnPenalty;

        // 找出過河兵：紅方在 row < 5（row 0-4）；黑方在 row > 4（row 5-9）
        int crossedCount = 0;
        foreach (int pos in pawnPositions)
        {
            if (IsCrossedRiver(pos, color)) crossedCount++;
        }

        foreach (int pos in pawnPositions)
        {
            int row = pos / BoardWidth;
            int col = pos % BoardWidth;

            // 1. 連兵加成：檢查左右相鄰列是否有友兵在相近排（row ±1 或同排）
            bool hasConnectedPawn = HasFriendlyPawnNearby(board, pos, color);
            if (hasConnectedPawn) score += ConnectedPawnBonus;

            // 2. 孤兵懲罰：左右列均無友兵（不限排）
            bool isIsolated = IsIsolatedPawn(board, pos, color);
            if (isIsolated) score -= IsolatedPawnPenalty;

            // 3. 過河兵協同加成：過河兵 >= 2 時每個過河兵額外加分
            if (crossedCount >= 2 && IsCrossedRiver(pos, color))
            {
                score += CrossedPawnCoordinationBonus;
            }
        }

        return score;
    }

    /// <summary>
    /// 判斷兵是否已過河。
    /// 紅方：在 row 0-4（敵方陣地）；黑方：在 row 5-9（敵方陣地）。
    /// </summary>
    private static bool IsCrossedRiver(int index, PieceColor color)
    {
        int row = index / BoardWidth;
        return color == PieceColor.Red ? row < 5 : row >= 5;
    }

    /// <summary>
    /// 判斷兵是否有相鄰友兵（相鄰列，row 差 <= 1）。
    /// </summary>
    private static bool HasFriendlyPawnNearby(IBoard board, int pawnIndex, PieceColor color)
    {
        int row = pawnIndex / BoardWidth;
        int col = pawnIndex % BoardWidth;

        // 檢查左右相鄰列
        for (int dc = -1; dc <= 1; dc += 2)
        {
            int adjCol = col + dc;
            if (adjCol < 0 || adjCol >= BoardWidth) continue;

            // 同排或相鄰排（-1, 0, +1）
            for (int dr = -1; dr <= 1; dr++)
            {
                int adjRow = row + dr;
                if (adjRow < 0 || adjRow >= BoardHeight) continue;

                var piece = board.GetPiece(adjRow * BoardWidth + adjCol);
                if (!piece.IsNone && piece.Type == PieceType.Pawn && piece.Color == color)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 判斷兵是否為孤兵（左右列均無友兵，不限排）。
    /// </summary>
    private static bool IsIsolatedPawn(IBoard board, int pawnIndex, PieceColor color)
    {
        int col = pawnIndex % BoardWidth;

        // 檢查左右相鄰列的所有排
        bool hasLeft = HasPawnInColumn(board, col - 1, color);
        bool hasRight = HasPawnInColumn(board, col + 1, color);

        return !hasLeft && !hasRight;
    }

    /// <summary>
    /// 判斷指定列是否有指定顏色的兵。
    /// </summary>
    private static bool HasPawnInColumn(IBoard board, int col, PieceColor color)
    {
        if (col < 0 || col >= BoardWidth) return false;

        for (int row = 0; row < BoardHeight; row++)
        {
            var piece = board.GetPiece(row * BoardWidth + col);
            if (!piece.IsNone && piece.Type == PieceType.Pawn && piece.Color == color)
            {
                return true;
            }
        }

        return false;
    }
}
