using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Nnue.Features;
using ChineseChess.Infrastructure.AI.Nnue.Helpers;
using ChineseChess.Infrastructure.AI.Nnue.Network;

namespace ChineseChess.Infrastructure.AI.Nnue.Evaluator;

/// <summary>
/// 使用 NNUE 推論的評估器。
///
/// 持有一個 per-instance NnueAccumulator 堆疊，配合搜尋樹的
/// MakeMove/UnmakeMove 做增量更新，避免每次 Evaluate 全量重算。
///
/// INnueNetwork 為無狀態推論核心，可多個 NnueEvaluator 實例共用。
/// </summary>
public sealed class NnueEvaluator : IEvaluator
{
    private readonly INnueNetwork network;
    private readonly NnueAccumulator accumulator = new();

    public NnueEvaluator(INnueNetwork network)
    {
        this.network = network;
    }

    /// <summary>為新 SearchWorker 建立帶獨立累加器的新實例（共用同一 INnueNetwork）。</summary>
    public IEvaluator CreateWorkerInstance() => new NnueEvaluator(network);

    // ── IEvaluator ───────────────────────────────────────────────────────

    public int Evaluate(IBoard board)
    {
        if (!network.IsLoaded)
            throw new InvalidOperationException("NNUE 模型尚未載入");

        return network.Evaluate(board, accumulator);
    }

    public int EvaluateFast(IBoard board) => Evaluate(board);

    // ── 累加器鉤子 ───────────────────────────────────────────────────────

    /// <summary>在 MakeMove 之後呼叫：Push 累加器並做增量（或全量）更新。</summary>
    public void OnMakeMove(IBoard board, Move move, Piece movedPiece, Piece capturedPiece)
    {
        if (!network.IsLoaded) return;
        var weights = network.Weights;
        if (weights is null) return;

        accumulator.Push();

        // 將/帥移動：king bucket 必定改變 → 直接全量刷新
        if (movedPiece.Type == PieceType.King)
        {
            accumulator.Refresh(board, weights);
            return;
        }

        // 非王移動：嘗試增量更新（若 bucket/mirror 改變則 fallback 至全量刷新）
        var cf0 = ComputeChangedFeatures(board, colorIdx: 0, move, movedPiece, capturedPiece);
        var cf1 = ComputeChangedFeatures(board, colorIdx: 1, move, movedPiece, capturedPiece);

        if (cf0.NeedsRefresh || cf1.NeedsRefresh)
        {
            accumulator.Refresh(board, weights);
            return;
        }

        accumulator.IncrementalUpdate(0, cf0.Added, cf0.Removed, weights);
        accumulator.IncrementalUpdate(1, cf1.Added, cf1.Removed, weights);
    }

    /// <summary>在 UnmakeMove 之前呼叫：退回累加器堆疊。</summary>
    public void OnUndoMove(IBoard board, Move move)
    {
        if (!network.IsLoaded) return;
        accumulator.Pop();
    }

    /// <summary>全量刷新累加器（搜尋開始時呼叫）。</summary>
    public void RefreshAccumulator(IBoard board)
    {
        if (!network.IsLoaded) return;
        var weights = network.Weights;
        if (weights is null) return;
        accumulator.Refresh(board, weights);
    }

    // ── 私有輔助 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 計算走棋後指定視角的 ChangedFeatures。
    /// 此方法在 MakeMove 之後呼叫，需從走棋後的棋盤狀態重建走棋前的 combinedBucket。
    ///
    /// 重建邏輯：
    ///   對非王移動，王的位置不變 → kingBucket 與 midMirror 在走棋前後相同。
    ///   攻擊 bucket 可能因被吃棋子而變化：若被吃棋子屬於當前視角，
    ///   則將其加回計算走棋前的 attackBucket。
    /// </summary>
    private static ChangedFeatures ComputeChangedFeatures(
        IBoard board,
        int colorIdx,
        Move move,
        Piece movedPiece,
        Piece capturedPiece)
    {
        var perspective = colorIdx == 0 ? PieceColor.Red : PieceColor.Black;
        var enemy = colorIdx == 0 ? PieceColor.Black : PieceColor.Red;

        int ownKing   = NnueFeatureHelper.FindKing(board, perspective);
        int enemyKing = NnueFeatureHelper.FindKing(board, enemy);
        if (ownKing < 0 || enemyKing < 0) return ChangedFeatures.RequiresRefresh;

        // midMirror 與 kingBucket：走棋前後相同（非王移動，王未動）
        bool midMirrorAfter = MidMirrorEncoder.RequiresMidMirror(board, perspective);
        var (kingBucket, mirrorAfter) = HalfKAv2Tables.GetKingBucket(ownKing, enemyKing, midMirrorAfter);

        // 走棋後的攻擊 bucket
        int attackBucketAfter = HalfKAv2Features.ComputeAttackBucket(board, perspective);

        // 重建走棋前的攻擊 bucket：若被吃棋子屬於此視角，加回其貢獻
        int attackBucketBefore = attackBucketAfter;
        if (!capturedPiece.IsNone && capturedPiece.Color == perspective)
        {
            bool hadRookBefore =
                (attackBucketAfter >= 2) ||
                (capturedPiece.Type == PieceType.Rook);
            bool hadKCBefore =
                ((attackBucketAfter & 1) != 0) ||
                (capturedPiece.Type is PieceType.Horse or PieceType.Cannon);
            attackBucketBefore = (hadRookBefore ? 2 : 0) + (hadKCBefore ? 1 : 0);
        }

        int combinedBucketBefore = kingBucket * HalfKAv2Tables.AttackBucketNb + attackBucketBefore;

        return HalfKAv2Features.GetChangedFeatures(
            board, perspective, move, movedPiece, capturedPiece,
            midMirrorBefore: midMirrorAfter,   // 相同（非王移動）
            combinedBucketBefore: combinedBucketBefore,
            mirrorBefore: mirrorAfter);        // 相同（非王移動）
    }

}
