using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Nnue.Training;

namespace ChineseChess.Infrastructure.AI.Nnue.Evaluator;

/// <summary>
/// 將 <see cref="TrainingNetwork"/> 包裝為 <see cref="IEvaluator"/>，
/// 供搜尋引擎在對局生成階段（VsHandcrafted / SelfPlay）使用。
///
/// 設計說明：
///   - 每次 Evaluate 執行完整前向傳播（無增量累加器），適合生成階段低深度搜尋
///   - CreateWorkerInstance() 返回 this，強制呼叫方使用 MaxThreads=1
///   - OnMakeMove/OnUndoMove/RefreshAccumulator 皆為 no-op（繼承自 IEvaluator 預設）
/// </summary>
public sealed class TrainingNetworkEvaluator : IEvaluator
{
    private readonly TrainingNetwork network;

    public TrainingNetworkEvaluator(TrainingNetwork network)
    {
        this.network = network;
    }

    public string Label => "NNUE(訓練中)";

    /// <summary>完整前向推論，回傳 centipawn 分數（先手視角）。</summary>
    public int Evaluate(IBoard board) => network.EvaluateToScore(board);

    /// <summary>與 Evaluate 相同（無快速路徑）。</summary>
    public int EvaluateFast(IBoard board) => network.EvaluateToScore(board);

    /// <summary>
    /// 返回 this。TrainingNetworkEvaluator 為單執行緒設計。
    /// 呼叫端必須確保 <c>SearchSettings.ThreadCount = 1</c>，
    /// 否則多個 SearchWorker 共用同一 TrainingNetwork 快取會導致梯度污染。
    /// </summary>
    /// <remarks>
    /// 刻意回傳 <c>this</c>：呼叫端必須保證 <c>SearchSettings.ThreadCount = 1</c>，
    /// 否則多個 SearchWorker 共用同一 TrainingNetwork 快取會導致梯度污染。
    /// 若未來需要多執行緒生成，應改為複製 network 權重後回傳新實例。
    /// </remarks>
    public IEvaluator CreateWorkerInstance() => this;
}
