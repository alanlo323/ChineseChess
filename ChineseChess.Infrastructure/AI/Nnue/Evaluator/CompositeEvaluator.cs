using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Nnue.Network;

namespace ChineseChess.Infrastructure.AI.Nnue.Evaluator;

/// <summary>
/// 複合評估器：NNUE 已載入時使用 NnueEvaluator，否則 fallback 至 HandcraftedEvaluator。
///
/// 設計原則：
///   - 切換即時生效（下次搜尋），無需重啟
///   - 搜尋中不允許切換（INnueNetwork 載入/卸載為同步操作，由 UI 層在搜尋間隙執行）
/// </summary>
public sealed class CompositeEvaluator : IEvaluator
{
    private readonly INnueNetwork network;
    private readonly HandcraftedEvaluator handcrafted;
    private readonly NnueEvaluator nnue;

    public CompositeEvaluator(INnueNetwork network)
    {
        this.network = network;
        handcrafted  = new HandcraftedEvaluator();
        nnue         = new NnueEvaluator(network);
    }

    // ── IEvaluator ───────────────────────────────────────────────────────

    public string Label => network.IsLoaded ? "NNUE" : "手工評估";

    public int Evaluate(IBoard board) =>
        network.IsLoaded ? nnue.Evaluate(board) : handcrafted.Evaluate(board);

    public int EvaluateFast(IBoard board) =>
        network.IsLoaded ? nnue.EvaluateFast(board) : handcrafted.EvaluateFast(board);

    public void OnMakeMove(IBoard board, Move move, Piece movedPiece, Piece capturedPiece)
    {
        if (network.IsLoaded) nnue.OnMakeMove(board, move, movedPiece, capturedPiece);
    }

    public void OnUndoMove(IBoard board, Move move)
    {
        if (network.IsLoaded) nnue.OnUndoMove(board, move);
    }

    public void RefreshAccumulator(IBoard board)
    {
        if (network.IsLoaded) nnue.RefreshAccumulator(board);
    }

    /// <summary>為新 SearchWorker 建立帶獨立 NnueEvaluator 實例（共用同一 INnueNetwork）。</summary>
    public IEvaluator CreateWorkerInstance() => new CompositeEvaluator(network);
}
