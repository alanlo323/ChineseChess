using ChineseChess.Domain.Entities;

namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// NNUE 訓練控制器：管理訓練生命週期，支援非同步啟動/暫停/恢復/停止。
///
/// 支援兩種資料來源模式：
///   - FromFile：從 TrainingDataLoader 載入靜態 .plain 檔案
///   - Generator：每 epoch 透過 IGameDataGenerator 動態生成對局資料
///     （VsHandcrafted 或 SelfPlay）
///
/// 訓練流程（每 epoch）：
///   1. [Generator 模式] 生成對局資料；[FromFile 模式] 使用已載入資料
///   2. 打亂訓練資料
///   3. 分批前向傳播計算損失
///   4. 反向傳播
///   5. Adam 更新
///   6. 若損失改善則回報並觸發存檔回調
/// </summary>
public sealed class NnueTrainer : IDisposable
{
    private readonly TrainingNetwork network;
    private readonly TrainingDataLoader? dataLoader;
    private readonly IGameDataGenerator? generator;
    private readonly int gameCount;
    private readonly int searchDepth;
    private readonly int searchTimeLimitMs;
    private readonly Action<TrainingProgress> progressCallback;
    private readonly Action<GameGenerationProgress>? generationProgressCallback;
    private readonly Action<TrainingNetwork>? bestModelCallback;

    // 訓練超參數
    private float learningRate;
    private readonly int batchSize;
    private readonly int epochCount;

    // 控制訊號
    private CancellationTokenSource? trainCts;
    private readonly ManualResetEventSlim pauseSignal = new(initialState: true);

    // volatile 確保背景訓練執行緒與 UI 執行緒之間狀態可見性
    private volatile bool isRunning;
    private volatile bool isPaused;
    private float bestLoss = float.MaxValue;

    public bool IsRunning  => isRunning;
    public bool IsPaused   => isPaused;
    public float BestLoss  => bestLoss;

    /// <summary>FromFile 模式建構子：從 .plain 檔案載入靜態訓練資料。</summary>
    public NnueTrainer(
        TrainingNetwork network,
        TrainingDataLoader dataLoader,
        Action<TrainingProgress> progressCallback,
        Action<TrainingNetwork>? bestModelCallback = null,
        float learningRate = 1e-3f,
        int batchSize = 256,
        int epochCount = 20)
    {
        this.network          = network;
        this.dataLoader       = dataLoader;
        this.progressCallback = progressCallback;
        this.bestModelCallback = bestModelCallback;
        this.learningRate     = learningRate;
        this.batchSize        = batchSize;
        this.epochCount       = epochCount;
    }

    /// <summary>Generator 模式建構子：每 epoch 透過 IGameDataGenerator 動態生成訓練資料。</summary>
    public NnueTrainer(
        TrainingNetwork network,
        IGameDataGenerator generator,
        int gameCount,
        Action<TrainingProgress> progressCallback,
        Action<GameGenerationProgress>? generationProgressCallback = null,
        Action<TrainingNetwork>? bestModelCallback = null,
        float learningRate = 1e-3f,
        int batchSize = 256,
        int epochCount = 20,
        int searchDepth = 4,
        int searchTimeLimitMs = 2000)
    {
        this.network                     = network;
        this.generator                   = generator;
        this.gameCount                   = gameCount;
        this.progressCallback            = progressCallback;
        this.generationProgressCallback  = generationProgressCallback;
        this.bestModelCallback           = bestModelCallback;
        this.learningRate                = learningRate;
        this.batchSize                   = batchSize;
        this.epochCount                  = epochCount;
        this.searchDepth                 = searchDepth;
        this.searchTimeLimitMs           = searchTimeLimitMs;
    }

    // ── 生命週期控制 ─────────────────────────────────────────────────

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;

        trainCts = new CancellationTokenSource();
        isRunning = true;
        isPaused  = false;
        pauseSignal.Set();

        return Task.Factory.StartNew(
            () => TrainLoop(trainCts.Token),
            trainCts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Pause()
    {
        if (!IsRunning || IsPaused) return;
        isPaused = true;
        pauseSignal.Reset();
    }

    public void Resume()
    {
        if (!IsPaused) return;
        isPaused = false;
        pauseSignal.Set();
    }

    public void Stop()
    {
        trainCts?.Cancel();
        pauseSignal.Set();   // 確保暫停中的訓練也能退出
        // 注意：isRunning / isPaused 由 TrainLoop 的 finally 區塊統一重設，
        // 此處不提前清除，避免 StartAsync() 的 IsRunning 守衛失效，
        // 導致舊背景任務仍在執行時被允許重啟第二個訓練迴圈。
    }

    // ── 主訓練迴圈 ───────────────────────────────────────────────────

    private void TrainLoop(CancellationToken ct)
    {
        try
        {
            // FromFile 模式：一次性載入全部資料
            List<TrainingPosition>? staticData = null;
            if (generator == null)
            {
                staticData = dataLoader!.LoadAllAsync(cancellationToken: ct)
                    .GetAwaiter().GetResult();

                if (staticData.Count == 0)
                {
                    ReportProgress(0, 0, 0, float.MaxValue, "訓練資料為空，請確認資料檔格式。", false);
                    return;
                }
            }

            var rng       = new Random(42);
            var sw        = System.Diagnostics.Stopwatch.StartNew();
            long totalSteps = 0;

            for (int epoch = 1; epoch <= epochCount && !ct.IsCancellationRequested; epoch++)
            {
                // Generator 模式：每 epoch 重新生成對局資料
                List<TrainingPosition> data;
                if (generator != null)
                {
                    ReportProgress(epoch, 0, 0, BestLoss,
                        $"Epoch {epoch}/{epochCount}：生成 {gameCount} 局對局資料中…", true);

                    data = generator.GenerateAsync(
                        gameCount, searchDepth, searchTimeLimitMs,
                        generationProgressCallback, ct).GetAwaiter().GetResult();

                    if (ct.IsCancellationRequested) break;

                    if (data.Count == 0)
                    {
                        ReportProgress(epoch, 0, 0, BestLoss, $"Epoch {epoch}：未生成任何局面，跳過。", true);
                        continue;
                    }
                }
                else
                {
                    data = staticData!;
                }

                // 打亂資料
                Shuffle(data, rng);

                float epochLoss = 0f;
                int   stepInEpoch = 0;
                long  totalStepsTarget = (long)epochCount * ((data.Count + batchSize - 1) / batchSize);

                for (int batchStart = 0; batchStart < data.Count && !ct.IsCancellationRequested; batchStart += batchSize)
                {
                    // 暫停等待
                    pauseSignal.Wait(ct);
                    if (ct.IsCancellationRequested) break;

                    float batchLoss = ProcessBatch(data, batchStart);
                    epochLoss += batchLoss;
                    stepInEpoch++;
                    totalSteps++;

                    // 每 100 步回報一次進度
                    if (stepInEpoch % 100 == 0)
                    {
                        double eta = EstimateEta(sw.Elapsed.TotalSeconds, totalSteps, totalStepsTarget);
                        ReportProgress(epoch, stepInEpoch, batchLoss, BestLoss,
                            $"Epoch {epoch}/{epochCount}，step {stepInEpoch}，loss={batchLoss:F4}",
                            true, lr: learningRate, eta: eta, totalSteps: totalSteps);
                    }
                }

                if (ct.IsCancellationRequested) break;

                float avgLoss = stepInEpoch > 0 ? epochLoss / stepInEpoch : float.MaxValue;
                if (avgLoss < bestLoss)
                {
                    bestLoss = avgLoss;
                    bestModelCallback?.Invoke(network);
                }

                ReportProgress(epoch, stepInEpoch, avgLoss, BestLoss,
                    $"Epoch {epoch} 完成，平均 loss={avgLoss:F4}，最佳={BestLoss:F4}",
                    true, lr: learningRate, totalSteps: totalSteps);

                // 學習率衰減（每 5 epoch × 0.5）
                if (epoch % 5 == 0) learningRate *= 0.5f;
            }
        }
        catch (OperationCanceledException)
        {
            ReportProgress(0, 0, 0, BestLoss, "訓練已停止。", false);
        }
        catch (NotImplementedException ex)
        {
            ReportProgress(0, 0, 0, BestLoss, $"訓練功能尚未完整實作：{ex.Message}", false);
        }
        catch (Exception ex)
        {
            ReportProgress(0, 0, 0, BestLoss, $"訓練發生未預期錯誤：{ex.GetType().Name} — {ex.Message}", false);
        }
        finally
        {
            isRunning = false;
            isPaused  = false;
        }
    }

    // ── 輔助 ─────────────────────────────────────────────────────────

    private void ReportProgress(
        int epoch, int step, float loss, float bestLoss, string? message, bool isRunning,
        float lr = 0f, double eta = -1, long totalSteps = 0)
    {
        progressCallback(new TrainingProgress
        {
            Epoch        = epoch,
            Step         = step,
            TotalSteps   = totalSteps,
            Loss         = loss,
            BestLoss     = bestLoss,
            LearningRate = lr,
            EtaSeconds   = eta,
            Message      = message,
            IsRunning    = isRunning,
        });
    }

    private static double EstimateEta(double elapsedSec, long done, long total)
    {
        if (done <= 0) return -1;
        return elapsedSec / done * (total - done);
    }

    /// <summary>
    /// 執行單一 batch 的前向 + 反向傳播，並更新 Adam 優化器。
    /// </summary>
    /// <param name="data">全部訓練資料。</param>
    /// <param name="batchStart">本次 batch 的起始索引。</param>
    /// <returns>本 batch 的平均 loss。</returns>
    private float ProcessBatch(List<TrainingPosition> data, int batchStart)
    {
        int end = Math.Min(batchStart + batchSize, data.Count);
        int actualSize = end - batchStart;

        network.ZeroGradients();

        float batchLoss = 0f;
        for (int k = batchStart; k < end; k++)
        {
            var pos = data[k];
            var board = new Board(pos.Fen);
            batchLoss += network.ForwardAndLoss(board, pos.Result, out _);
            network.Backward(board, pos.Result, actualSize);
        }

        network.StepAdam(learningRate);
        return batchLoss / actualSize;
    }

    private static void Shuffle<T>(List<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public void Dispose()
    {
        Stop();
        trainCts?.Dispose();
        pauseSignal.Dispose();
    }
}
