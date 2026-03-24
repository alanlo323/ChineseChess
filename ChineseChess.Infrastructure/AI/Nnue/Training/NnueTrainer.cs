using ChineseChess.Domain.Entities;

namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// NNUE 訓練控制器：管理訓練生命週期，支援非同步啟動/暫停/恢復/停止。
///
/// 訓練流程（每 epoch）：
///   1. 打亂訓練資料
///   2. 分批前向傳播計算損失
///   3. 反向傳播（目前為框架實作，完整梯度留後補）
///   4. Adam 更新
///   5. 若損失改善則回報並觸發存檔回調
/// </summary>
public sealed class NnueTrainer : IDisposable
{
    private readonly TrainingNetwork network;
    private readonly TrainingDataLoader dataLoader;
    private readonly Action<TrainingProgress> progressCallback;
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
        isRunning = false;
        isPaused  = false;
    }

    // ── 主訓練迴圈 ───────────────────────────────────────────────────

    private void TrainLoop(CancellationToken ct)
    {
        try
        {
            var data = dataLoader.LoadAllAsync(cancellationToken: ct)
                .GetAwaiter().GetResult();

            if (data.Count == 0)
            {
                ReportProgress(0, 0, 0, float.MaxValue, "訓練資料為空，請確認資料檔格式。", false);
                return;
            }

            var rng       = new Random(42);
            var sw        = System.Diagnostics.Stopwatch.StartNew();
            long totalSteps = 0;

            for (int epoch = 1; epoch <= epochCount && !ct.IsCancellationRequested; epoch++)
            {
                // 打亂資料
                Shuffle(data, rng);

                float epochLoss = 0f;
                int   stepInEpoch = 0;

                for (int batchStart = 0; batchStart < data.Count && !ct.IsCancellationRequested; batchStart += batchSize)
                {
                    // 暫停等待
                    pauseSignal.Wait(ct);
                    if (ct.IsCancellationRequested) break;

                    int end = Math.Min(batchStart + batchSize, data.Count);
                    float batchLoss = 0f;

                    network.ZeroGradients();

                    for (int k = batchStart; k < end; k++)
                    {
                        var pos = data[k];
                        var board = new Board(pos.Fen);
                        float loss = network.ForwardAndLoss(board, pos.Result, out _);
                        batchLoss += loss;
                        network.Backward(board, pos.Result, end - batchStart);
                    }

                    network.StepAdam(learningRate);

                    batchLoss /= (end - batchStart);
                    epochLoss += batchLoss;
                    stepInEpoch++;
                    totalSteps++;

                    // 每 100 步回報一次進度
                    if (stepInEpoch % 100 == 0)
                    {
                        double eta = EstimateEta(sw.Elapsed.TotalSeconds, totalSteps,
                            (long)epochCount * ((data.Count + batchSize - 1) / batchSize));
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
