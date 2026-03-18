using ChineseChess.Application.Enums;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using System.Collections.Generic;

namespace ChineseChess.Application.Services;

/// <summary>
/// WXF 著法分類器。
/// 在 MakeMove 執行後呼叫，根據走法特性分類為 Cancel / Idle / Chase / Check。
///
/// 棋子價值（與 SearchWorker 對齊）：
///   King=10000, Advisor=120, Elephant=120, Horse=270, Rook=600, Cannon=285, Pawn=30
/// </summary>
public static class MoveClassifier
{
    // 棋子價值：索引對應 PieceType 枚舉值 (None=0, King=1, ..., Pawn=7)
    internal static readonly int[] PieceValues = { 0, 10000, 120, 120, 270, 600, 285, 30 };

    // 棋盤寬度（象棋固定 9 格）
    private const int BoardWidth = 9;

    /// <summary>
    /// 分類一步著法。
    /// 必須在 boardAfterMove.MakeMove(move) 之後呼叫（此時 boardAfterMove.Turn = 對手）。
    /// </summary>
    /// <param name="boardAfterMove">MakeMove 後的棋盤狀態。</param>
    /// <param name="move">剛執行的著法。</param>
    /// <param name="movedPiece">移動的棋子（MakeMove 前取得）。</param>
    /// <param name="capturedPiece">被吃的棋子（MakeMove 前取得；若無吃子則為 Piece.None）。</param>
    /// <param name="victimSquare">Chase 時被追棋子的格子索引；非 Chase 時為 -1。</param>
    public static MoveClassification Classify(
        IBoard boardAfterMove,
        Move move,
        Piece movedPiece,
        Piece capturedPiece,
        out int victimSquare)
    {
        victimSquare = -1;

        // 1. 吃子 → Cancel（不可逆）
        if (!capturedPiece.IsNone)
            return MoveClassification.Cancel;

        // 2. 兵前進 → Cancel；兵橫移 → 繼續向下判斷（視為 Idle 或 Chase）
        if (movedPiece.Type == PieceType.Pawn && IsPawnAdvance(move, movedPiece.Color))
            return MoveClassification.Cancel;

        // 3. 走完後對手被將軍 → Check
        //    boardAfterMove.Turn = 對手，IsCheck(Turn) 即檢查對手
        if (boardAfterMove.IsCheck(boardAfterMove.Turn))
            return MoveClassification.Check;

        // 4. 追擊對方未受保護棋子 → Chase
        if (TryFindChaseVictim(boardAfterMove, move, movedPiece, out victimSquare))
            return MoveClassification.Chase;

        return MoveClassification.Idle;
    }

    /// <summary>
    /// 判斷兵的著法是否為「前進」（不可逆）。
    /// 前進：同列且往對方陣地方向（紅兵向上 row 減小，黑卒向下 row 增大）。
    /// 橫移：不同列（fromCol != toCol），視為可逆的 Idle。
    /// </summary>
    private static bool IsPawnAdvance(Move move, PieceColor color)
    {
        int fromCol = move.From % BoardWidth;
        int toCol   = move.To   % BoardWidth;
        if (fromCol != toCol) return false; // 橫移，不是前進

        int fromRow = move.From / BoardWidth;
        int toRow   = move.To   / BoardWidth;
        return color == PieceColor.Red
            ? toRow < fromRow   // 紅兵向上（row 減小）
            : toRow > fromRow;  // 黑卒向下（row 增大）
    }

    /// <summary>
    /// 嘗試找到被追擊的單一目標。
    /// 若恰好有一個未受保護的可追棋子受到威脅，則回傳 true 並設定 victimSquare。
    /// 若沒有或有多個，則回傳 false（Idle）。
    ///
    /// 可追棋子類型（WXF 規定）：車、馬、炮、兵（仕/象/將 不算追擊目標）。
    /// </summary>
    private static bool TryFindChaseVictim(
        IBoard board, Move move, Piece movedPiece, out int victimSquare)
    {
        victimSquare = -1;
        int attackerSquare = move.To;
        int attackerValue  = PieceValues[(int)movedPiece.Type];

        // 切換到攻擊方視角，找從 attackerSquare 出發的合法吃子著法
        board.MakeNullMove();
        var captureMoves = new List<Move>();
        foreach (var m in board.GenerateLegalMoves())
        {
            if (m.From == attackerSquare && !board.GetPiece(m.To).IsNone)
                captureMoves.Add(m);
        }
        board.UnmakeNullMove();

        // 此時 board.Turn = 防守方（同 MakeMove 後狀態）
        var candidates = new List<int>();
        foreach (var capture in captureMoves)
        {
            int targetSquare = capture.To;
            var targetPiece  = board.GetPiece(targetSquare);

            // 跳過 King（King 被攻擊屬於 Check，已在上層處理）
            if (targetPiece.Type == PieceType.King) continue;

            // 跳過仕/象（WXF 規定這兩類棋子不屬於追擊目標）
            if (targetPiece.Type == PieceType.Advisor ||
                targetPiece.Type == PieceType.Elephant) continue;

            // 若目標已受保護（防守方有足夠棋子守護），跳過
            if (IsVictimProtected(board, attackerSquare, attackerValue, targetSquare))
                continue;

            candidates.Add(targetSquare);
        }

        if (candidates.Count == 1)
        {
            victimSquare = candidates[0];
            return true;
        }

        return false; // 0 個或多個候選 → Idle
    }

    /// <summary>
    /// 判斷目標棋子是否受到有效保護。
    /// 方法：假設攻擊方吃掉目標，再檢查防守方是否有價值 ≤ 攻擊方的棋子可以回吃。
    ///
    /// 「有效保護」= 防守方有棋子可吃掉攻擊方，且那顆棋子的價值 ≤ 攻擊方價值
    ///             （意即回吃不虧材，防守方有誘因守護目標）。
    /// </summary>
    private static bool IsVictimProtected(
        IBoard board, int attackerSquare, int attackerValue, int victimSquare)
    {
        // 暫時讓攻擊方吃掉目標，再從防守方視角查是否能回吃
        var captureMove = new Move(attackerSquare, victimSquare);
        board.MakeNullMove();            // → 攻擊方輪次
        board.MakeMove(captureMove);     // 攻擊方吃目標 → 防守方輪次

        bool isProtected = false;
        foreach (var defMove in board.GenerateLegalMoves())
        {
            if (defMove.To != victimSquare) continue; // 只看能回吃到 victimSquare 的著法
            var defPiece = board.GetPiece(defMove.From);
            if (PieceValues[(int)defPiece.Type] <= attackerValue)
            {
                isProtected = true;
                break;
            }
        }

        board.UnmakeMove(captureMove);   // 撤銷吃子
        board.UnmakeNullMove();          // 還原輪次

        return isProtected;
    }
}
