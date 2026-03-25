using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Nnue.Training;

namespace ChineseChess.Tests.Infrastructure.Nnue;


/// <summary>
/// 驗證 TrainingNetwork 的反向傳播正確性：
///   1. 梯度累積後 Adam 更新可使損失在訓練中持續下降
///   2. 反向傳播後所有梯度值均有限（無 NaN / Infinity）
///   3. ZeroGradients 確實清除所有梯度
/// </summary>
public class NnueTrainingTests
{
    // 初始局面 FEN（紅方先走）
    private const string InitialFen =
        "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    [Fact]
    public void LossDecreases_AfterTenBatches()
    {
        var board   = new Board(InitialFen);
        var network = new TrainingNetwork();
        const float target = 1.0f;
        const float lr     = 1e-3f;

        float firstLoss  = float.MaxValue;
        float latestLoss = float.MaxValue;

        for (int step = 0; step < 10; step++)
        {
            network.ZeroGradients();
            float loss = network.ForwardAndLoss(board, target, out _);
            network.Backward(board, target, batchSize: 1f);
            network.StepAdam(lr);

            if (step == 0) firstLoss  = loss;
            latestLoss = loss;
        }

        Assert.True(latestLoss < firstLoss,
            $"10 步訓練後損失應下降，但 first={firstLoss:F6} latest={latestLoss:F6}");
    }

    [Fact]
    public void Backward_NoNaNOrInfInGradients()
    {
        var board   = new Board(InitialFen);
        var network = new TrainingNetwork();
        const float target = 0.5f;

        network.ZeroGradients();
        network.ForwardAndLoss(board, target, out _);
        network.Backward(board, target, batchSize: 1f);

        Assert.True(network.AllGradientsFinite(),
            "Backward() 後所有梯度陣列均不應含 NaN 或 Infinity");
    }

    [Fact]
    public void ZeroGradients_ClearsAllGradientArrays()
    {
        var board   = new Board(InitialFen);
        var network = new TrainingNetwork();
        const float target = 1.0f;

        // 先執行一次 Backward 讓梯度非零
        network.ZeroGradients();
        network.ForwardAndLoss(board, target, out _);
        network.Backward(board, target, batchSize: 1f);

        // 清零後應全部為零
        network.ZeroGradients();
        Assert.True(network.AllGradientsZero(),
            "ZeroGradients() 後所有梯度陣列均應為零");
    }
}

/// <summary>
/// 驗證 NnueTrainer 使用 IGameDataGenerator 模式的整合行為：
///   1. 使用 generator 的訓練迴圈，單 epoch 後損失為有限值
///   2. generationProgressCallback 在生成階段被呼叫
/// </summary>
[Collection("NnueGeneration")]
public class NnueTrainerGeneratorTests
{
    private const string InitialFen =
        "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    [Fact]
    public async Task TrainWithGenerator_OneEpoch_LossIsFinite()
    {
        var network   = new TrainingNetwork();
        var generator = new SelfPlayGenerator(network, randomOpeningMoves: 4);
        TrainingProgress? lastProgress = null;

        var trainer = new NnueTrainer(
            network:                      network,
            generator:                    generator,
            gameCount:                    2,
            progressCallback:             p => lastProgress = p,
            generationProgressCallback:   null,
            bestModelCallback:            null,
            learningRate:                 1e-3f,
            batchSize:                    8,
            epochCount:                   1,
            searchDepth:                  1,
            searchTimeLimitMs:            3000);

        await trainer.StartAsync();

        Assert.NotNull(lastProgress);
        Assert.True(float.IsFinite(lastProgress!.Loss) || float.IsFinite(lastProgress.BestLoss),
            $"單 epoch 後損失應為有限值，但 loss={lastProgress.Loss}, bestLoss={lastProgress.BestLoss}");
    }

    [Fact]
    public async Task TrainWithGenerator_GenerationProgress_IsReported()
    {
        var network   = new TrainingNetwork();
        var generator = new SelfPlayGenerator(network, randomOpeningMoves: 4);
        int generationCallCount = 0;

        var trainer = new NnueTrainer(
            network:                      network,
            generator:                    generator,
            gameCount:                    2,
            progressCallback:             _ => { },
            generationProgressCallback:   _ => generationCallCount++,
            bestModelCallback:            null,
            learningRate:                 1e-3f,
            batchSize:                    8,
            epochCount:                   1,
            searchDepth:                  1,
            searchTimeLimitMs:            3000);

        await trainer.StartAsync();

        Assert.True(generationCallCount >= 2,
            $"生成 2 局應至少觸發 2 次 generationProgressCallback，但只有 {generationCallCount} 次");
    }
}
