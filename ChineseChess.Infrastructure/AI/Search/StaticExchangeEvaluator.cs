using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;

namespace ChineseChess.Infrastructure.AI.Search;

/// <summary>
/// 靜態交換評估（Static Exchange Evaluation, SEE）。
///
/// 模擬目標格上的連續吃子序列，評估第一步吃子的淨材料收益。
/// 雙方均採用「最小代價攻擊者優先」策略，且均可選擇在任何時間點停止交換。
///
/// 適用於象棋（包含炮的跳吃特性）：
///   - 使用 IBoard.GenerateLegalMoves() 確保只考慮合法著法，
///     炮的跳吃限制（需炮台）由走法生成器處理，無需額外特判。
///   - 被釘住的棋子不會出現在合法著法中，SEE 略偏保守但絕對安全。
/// </summary>
internal static class StaticExchangeEvaluator
{
    /// <summary>
    /// 對指定吃子著法執行靜態交換評估。
    /// </summary>
    /// <param name="board">當前局面（不被修改：Make/Unmake 成對使用）。</param>
    /// <param name="firstCapture">欲評估的吃子著法。</param>
    /// <param name="pieceValues">棋子價值陣列（0-indexed by PieceType）。</param>
    /// <returns>
    /// 從行棋方視角的淨材料收益：
    ///   正值 = 有利吃子，負值 = 不利吃子，0 = 均等交換。
    ///   非吃子著法回傳 0。
    /// </returns>
    internal static int See(IBoard board, Move firstCapture, int[] pieceValues)
    {
        var capturedPiece = board.GetPiece(firstCapture.To);
        if (capturedPiece.IsNone) return 0;

        var movingPiece = board.GetPiece(firstCapture.From);
        if (movingPiece.IsNone) return 0;

        int capturedValue = pieceValues[(int)capturedPiece.Type];
        int movingValue = pieceValues[(int)movingPiece.Type];

        // 執行第一步吃子，輪到對手
        board.MakeMove(firstCapture);

        // 對手若選擇回吃，可得到 movingValue；但我方可再回吃…雙方遞迴最優選擇
        int opponentGain = RecaptureGain(board, firstCapture.To, movingValue, pieceValues);

        board.UnmakeMove(firstCapture);

        // 我方得 capturedValue，但對手若有利可圖也會回吃
        return capturedValue - Math.Max(0, opponentGain);
    }

    /// <summary>
    /// 遞迴計算：從 board.Turn 的視角，在 targetSquare 發動回吃的最大淨收益。
    /// </summary>
    /// <param name="prevCapturedValue">剛進入 targetSquare 的棋子價值（回吃可得之數）。</param>
    private static int RecaptureGain(IBoard board, int targetSquare, int prevCapturedValue, int[] pieceValues)
    {
        var cheapest = FindCheapestCapture(board, targetSquare, pieceValues);
        if (cheapest.IsNull) return 0;  // 無法回吃

        var attacker = board.GetPiece(cheapest.From);
        int attackerValue = pieceValues[(int)attacker.Type];

        board.MakeMove(cheapest);
        int opponentGain = RecaptureGain(board, targetSquare, attackerValue, pieceValues);
        board.UnmakeMove(cheapest);

        // 行棋方得 prevCapturedValue，但對手可選擇繼續回吃
        return prevCapturedValue - Math.Max(0, opponentGain);
    }

    /// <summary>
    /// 找到對 targetSquare 發動吃子的最低價值棋子（最佳回吃選擇）。
    /// </summary>
    private static Move FindCheapestCapture(IBoard board, int targetSquare, int[] pieceValues)
    {
        Move best = Move.Null;
        int minValue = int.MaxValue;

        foreach (var move in board.GenerateLegalMoves())
        {
            if (move.To != targetSquare) continue;
            if (board.GetPiece(move.To).IsNone) continue;  // 確保是吃子著法

            var attacker = board.GetPiece(move.From);
            if (attacker.IsNone) continue;

            int value = pieceValues[(int)attacker.Type];
            if (value < minValue)
            {
                minValue = value;
                best = move;
            }
        }

        return best;
    }
}
