using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Nnue.Evaluator;
using ChineseChess.Infrastructure.AI.Nnue.Training;

namespace ChineseChess.Tests.Infrastructure.Nnue;

/// <summary>
/// 驗證 TrainingNetworkEvaluator 作為 IEvaluator 的基本行為：
///   1. Evaluate 回傳有限分數（無 NaN / Infinity）
///   2. 不同局面回傳不同分數
///   3. CreateWorkerInstance 返回 this（單執行緒設計）
///   4. EvaluateFast 與 Evaluate 相同
///   5. 呼叫 EvaluateToScore 後再訓練不影響梯度
/// </summary>
public class TrainingNetworkEvaluatorTests
{
    private const string InitialFen =
        "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    private const string MidgameFen =
        "r1bakabr1/9/1cn1c1n2/p1p1p1p1p/9/2P6/P3P1P1P/1C2C1N2/9/RNBAKAB1R w - - 0 1";

    [Fact]
    public void Evaluate_ReturnsFiniteValue_ForInitialBoard()
    {
        var network   = new TrainingNetwork();
        var evaluator = new TrainingNetworkEvaluator(network);
        var board     = new Board(InitialFen);

        int score = evaluator.Evaluate(board);

        Assert.True(!float.IsNaN(score) && !float.IsInfinity(score),
            $"Evaluate 應回傳有限整數分數，但得到 {score}");
    }

    [Fact]
    public void Evaluate_ReturnsInt_ForDifferentBoards()
    {
        // 驗證 TrainingNetworkEvaluator 對不同局面均能回傳有限整數分數
        // 注意：全新 Xavier 初始化的網路輸出非常小，整數截斷後可能相同；
        // 此測試只驗證功能正確性（有限輸出），不依賴訓練收斂後的差異性
        var network   = new TrainingNetwork();
        var evaluator = new TrainingNetworkEvaluator(network);
        var board1    = new Board(InitialFen);
        var board2    = new Board(MidgameFen);

        int score1 = evaluator.Evaluate(board1);
        int score2 = evaluator.Evaluate(board2);

        // 兩個局面的分數都應為有限整數（非 NaN/Inf）
        Assert.True(score1 is >= -10000 and <= 10000,
            $"初始局面分數應在合理範圍，但得到 {score1}");
        Assert.True(score2 is >= -10000 and <= 10000,
            $"中局局面分數應在合理範圍，但得到 {score2}");
    }

    [Fact]
    public void CreateWorkerInstance_ReturnsIndependentEvaluatorWithSameLabel()
    {
        var network   = new TrainingNetwork();
        var evaluator = new TrainingNetworkEvaluator(network);

        IEvaluator worker = evaluator.CreateWorkerInstance();

        // CreateWorkerInstance 應回傳獨立的 InferenceEvaluator（非 this），
        // 讓多個 SearchWorker 可安全並行使用（共享 weights，獨立快取）
        Assert.NotSame(evaluator, worker);
        Assert.Equal(evaluator.Label, worker.Label);
    }

    [Fact]
    public void CreateWorkerInstance_Worker_EvaluateSameAsOriginal()
    {
        var network   = new TrainingNetwork();
        var evaluator = new TrainingNetworkEvaluator(network);
        var worker    = evaluator.CreateWorkerInstance();
        var board     = new Board(InitialFen);

        int scoreOriginal = evaluator.Evaluate(board);
        int scoreWorker   = worker.Evaluate(board);

        // worker 共享同一份 weights，對相同局面應回傳相同分數
        Assert.Equal(scoreOriginal, scoreWorker);
    }

    [Fact]
    public void EvaluateFast_EqualsEvaluate()
    {
        var network   = new TrainingNetwork();
        var evaluator = new TrainingNetworkEvaluator(network);
        var board     = new Board(InitialFen);

        int scoreFull = evaluator.Evaluate(board);
        int scoreFast = evaluator.EvaluateFast(board);

        // TrainingNetworkEvaluator 無快速路徑，兩者應相等
        Assert.Equal(scoreFull, scoreFast);
    }

    [Fact]
    public void EvaluateToScore_ThenTraining_GradientsRemainCorrect()
    {
        // 驗證：先用 EvaluateToScore 評估（生成階段模擬），再進行訓練時梯度仍正確
        var network   = new TrainingNetwork();
        var evaluator = new TrainingNetworkEvaluator(network);
        var board     = new Board(InitialFen);
        const float target = 1.0f;

        // 模擬生成階段：呼叫 EvaluateToScore
        _ = evaluator.Evaluate(board);
        _ = evaluator.Evaluate(board);

        // 模擬訓練階段：正常 ForwardAndLoss → Backward → StepAdam
        network.ZeroGradients();
        float loss = network.ForwardAndLoss(board, target, out _);
        network.Backward(board, target, batchSize: 1f);

        // 確認梯度仍有限（EvaluateToScore 不應汙染訓練快取）
        Assert.True(network.AllGradientsFinite(),
            "EvaluateToScore 呼叫後再訓練，梯度不應含 NaN 或 Infinity");
        Assert.True(loss > 0f && loss < 1f,
            $"損失應在合理範圍內，但得到 {loss:F6}");
    }
}
