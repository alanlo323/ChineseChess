using ChineseChess.Domain.Entities;

namespace ChineseChess.Infrastructure.AI.Evaluators;

public interface IEvaluator
{
    int Evaluate(IBoard board);

    /// <summary>
    /// 快速評估：只含 Material + PST（含 GamePhase 插值）。
    /// 跳過王安全、機動力、兵型、炮威脅、馬腳、車壓制、棋子協同、空間控制。
    /// 用於 Lazy Evaluation 中的 Razor/Futility 剪枝預篩。
    /// </summary>
    int EvaluateFast(IBoard board);

    // ── NNUE 增量累加器鉤子（預設 no-op，HandcraftedEvaluator 不需修改）─────

    /// <summary>在 MakeMove 之後呼叫。實作可選擇更新累加器（增量或全量）。</summary>
    void OnMakeMove(IBoard board, Move move, Piece movedPiece, Piece capturedPiece) { }

    /// <summary>在 UnmakeMove 之前呼叫。實作可選擇退回累加器堆疊。</summary>
    void OnUndoMove(IBoard board, Move move) { }

    /// <summary>全量刷新累加器（搜尋開始時或王移動後呼叫）。</summary>
    void RefreshAccumulator(IBoard board) { }

    /// <summary>
    /// 為新的 SearchWorker 建立專屬實例。
    /// 無狀態評估器（HandcraftedEvaluator）可返回 this；
    /// 有狀態評估器（NnueEvaluator）必須返回帶獨立 NnueAccumulator 的新實例。
    /// </summary>
    IEvaluator CreateWorkerInstance() => this;
}
