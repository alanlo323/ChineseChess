using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Nnue.Training;

namespace ChineseChess.Infrastructure.AI.Nnue.Evaluator;

/// <summary>
/// 將 <see cref="TrainingNetwork"/> 包裝為 <see cref="IEvaluator"/>，
/// 供搜尋引擎在對局生成階段（VsHandcrafted / SelfPlay）使用。
///
/// 設計說明：
///   - 每次 Evaluate 執行完整前向傳播（無增量累加器），適合生成階段低深度搜尋。
///   - <see cref="CreateWorkerInstance"/> 回傳包裝 <see cref="TrainingNetworkInferenceView"/>
///     的獨立副本，支援多個 <see cref="Search.SearchWorker"/> 並行使用（Lazy SMP 安全）。
///   - OnMakeMove/OnUndoMove/RefreshAccumulator 皆為 no-op（繼承自 IEvaluator 預設）。
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
    /// 回傳包裝 <see cref="TrainingNetworkInferenceView"/> 的獨立評估器。
    /// 每個 worker 持有自己的推論快取，共享同一份 weights（唯讀），執行緒安全。
    /// </summary>
    public IEvaluator CreateWorkerInstance()
        => new InferenceEvaluator(new TrainingNetworkInferenceView(network), network);

    // ── 內部 worker 評估器 ────────────────────────────────────────────

    /// <summary>
    /// 由 <see cref="TrainingNetworkEvaluator.CreateWorkerInstance"/> 建立的 worker 實例。
    /// 持有獨立的 <see cref="TrainingNetworkInferenceView"/>，可安全從多個執行緒同時使用。
    /// 分叉自身時建立新的 InferenceView（共享同一份唯讀 weights），
    /// 保證即使 SearchEngine 在 ThreadCount &gt; 1 時多次呼叫 CreateWorkerInstance，
    /// 每個 SearchWorker 都持有完全獨立的可變快取。
    /// </summary>
    private sealed class InferenceEvaluator : IEvaluator
    {
        private readonly TrainingNetworkInferenceView inferenceView;
        private readonly TrainingNetwork network;

        internal InferenceEvaluator(TrainingNetworkInferenceView inferenceView, TrainingNetwork network)
        {
            this.inferenceView = inferenceView;
            this.network       = network;
        }

        public string Label => "NNUE(訓練中)";

        public int Evaluate(IBoard board)     => inferenceView.EvaluateToScore(board);
        public int EvaluateFast(IBoard board) => inferenceView.EvaluateToScore(board);

        /// <summary>建立新的 InferenceView 副本（共享 weights，獨立快取），執行緒安全。</summary>
        public IEvaluator CreateWorkerInstance()
            => new InferenceEvaluator(new TrainingNetworkInferenceView(network), network);
    }
}
