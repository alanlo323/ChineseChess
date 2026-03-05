using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Infrastructure.AI.Search;

public class SearchEngine : IAiEngine
{
    private readonly IEvaluator _evaluator;
    private readonly TranspositionTable _tt;

    private const int HeartbeatIntervalMs = 100;

    private sealed class SearchProgressState
    {
        public int CurrentDepth;
        public int Score;
        public string? BestMove;
    }

    public SearchEngine()
    {
        _evaluator = new HandcraftedEvaluator();
        _tt = new TranspositionTable(64);
    }

    public Task<SearchResult> SearchAsync(IBoard board, SearchSettings settings, CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
    {
        return Task.Run(() =>
        {
            int threadCount = Math.Clamp(settings.ThreadCount, 1, 128);
            _tt.NewGeneration();
            var pauseSignal = settings.PauseSignal ?? new ManualResetEventSlim(true);

            // 用獨立的 timeLimitCts 管理思考時間，讓監控任務依「實際搜尋時間」取消
            // 暫停期間不計入 time limit，恢復後繼續剩餘思考時間
            using var timeLimitCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeLimitCts.Token);
            var token = linkedCts.Token;

            // --- 建立主 worker ---
            // ct = 使用者明確停止 token（hardStopCt），token = 時間限制 + 使用者停止（合併）
            var mainWorker = new SearchWorker(board.Clone(), _evaluator, _tt, token, ct, pauseSignal);

            // --- 啟動輔助 worker（各自獨立執行迭代加深） ---
            var helperWorkers = new SearchWorker[threadCount - 1];
            var helperTasks = new Task[threadCount - 1];

            for (int i = 0; i < threadCount - 1; i++)
            {
                var helper = new SearchWorker(board.Clone(), _evaluator, _tt, token, ct, pauseSignal);
                helperWorkers[i] = helper;

                int helperDepth = settings.Depth + 1 + (i % 2);
                helperTasks[i] = Task.Factory.StartNew(
                    () => helper.Search(helperDepth),
                    token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            // --- 主 worker：以迭代加深並回報進度 ---
            var result = new SearchResult();
            var stopwatch = Stopwatch.StartNew();

            // 暫停時間追蹤：用實際搜尋時間（排除暫停期間）回報 ElapsedMs
            long totalPausedMs = 0L;       // 已累計的暫停總時間
            long pauseStartedAtMs = -1L;   // 目前暫停開始的時間點（-1 = 未暫停）
            var pauseTimingLock = new object();

            long GetActiveElapsedMs()
            {
                lock (pauseTimingLock)
                {
                    var total = stopwatch.ElapsedMilliseconds;
                    var currentPause = pauseStartedAtMs >= 0 ? total - pauseStartedAtMs : 0L;
                    var activeElapsed = total - totalPausedMs - currentPause;
                    return activeElapsed > 0 ? activeElapsed : 0L;
                }
            }

            void RegisterPauseStart()
            {
                lock (pauseTimingLock)
                {
                    if (pauseStartedAtMs < 0)
                    {
                        pauseStartedAtMs = stopwatch.ElapsedMilliseconds;
                    }
                }
            }

            void RegisterPauseEnd()
            {
                lock (pauseTimingLock)
                {
                    if (pauseStartedAtMs >= 0)
                    {
                        totalPausedMs += stopwatch.ElapsedMilliseconds - pauseStartedAtMs;
                        pauseStartedAtMs = -1L;
                    }
                }
            }

            var progressState = new SearchProgressState();
            var progressStateLock = new object();
            System.Timers.Timer? heartbeatTimer = null;

            long GetTotalNodes()
            {
                long total = mainWorker.NodesVisited;
                for (int i = 0; i < helperWorkers.Length; i++)
                    total += helperWorkers[i].NodesVisited;
                return total;
            }

            void ReportProgress(bool isHeartbeat, int depth, int score, string? bestMove)
            {
                if (progress == null) return;

                int reportDepth = depth;
                int reportScore = score;
                string? reportBestMove = bestMove;

                if (isHeartbeat)
                {
                    lock (progressStateLock)
                    {
                        reportDepth = progressState.CurrentDepth;
                        reportScore = progressState.Score;
                        reportBestMove = progressState.BestMove;
                    }
                }

                long nodes = GetTotalNodes();
                long elapsedMs = GetActiveElapsedMs(); // 排除暫停時間
                long nodesPerSecond = elapsedMs > 0 ? (nodes * 1000L) / elapsedMs : 0;

                progress.Report(new SearchProgress
                {
                    CurrentDepth = reportDepth,
                    MaxDepth = settings.Depth,
                    Nodes = nodes,
                    Score = reportScore,
                    BestMove = reportBestMove,
                    ElapsedMs = elapsedMs,
                    NodesPerSecond = nodesPerSecond,
                    IsHeartbeat = isHeartbeat,
                    ThreadCount = threadCount,
                    Message = isHeartbeat
                        ? "Heartbeat report"
                        : $"Depth {depth}/{settings.Depth} computed ({threadCount} threads)"
                });
            }

            if (progress != null)
            {
                heartbeatTimer = new System.Timers.Timer(HeartbeatIntervalMs);
                heartbeatTimer.Elapsed += (_, _) =>
                {
                    if (token.IsCancellationRequested) return;
                    if (!pauseSignal.IsSet) return; // 暫停中不回報進度，避免誤導使用者
                    try { ReportProgress(true, 0, 0, null); }
                    catch { /* 忽略背景進度回報可忽略的失敗。 */ }
                };
                heartbeatTimer.Start();
                if (pauseSignal.IsSet) ReportProgress(true, 0, 0, null);
            }

            // 啟動「有效時間監控」任務：只在 AI 實際搜尋時計時，暫停時凍結倒數
            Task? timeLimitMonitor = null;
            if (settings.TimeLimitMs > 0)
            {
                if (!pauseSignal.IsSet)
                {
                    RegisterPauseStart();
                }

                timeLimitMonitor = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            if (!pauseSignal.IsSet)
                            {
                                RegisterPauseStart();
                                try { pauseSignal.Wait(ct); }
                                catch (OperationCanceledException)
                                {
                                    return;
                                }
                                RegisterPauseEnd();
                                continue;
                            }

                            if (GetActiveElapsedMs() >= settings.TimeLimitMs)
                            {
                                timeLimitCts.Cancel();
                                return;
                            }

                            token.WaitHandle.WaitOne(10);
                        }
                    }
                    finally
                    {
                        RegisterPauseEnd();
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            try
            {
                for (int depth = 1; depth <= settings.Depth; depth++)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    int score = mainWorker.SearchSingleDepth(depth);

                    var bestMove = mainWorker.ProbeBestMove();

                    result.BestMove = bestMove;
                    result.Score = score;
                    result.Depth = depth;
                    result.Nodes = GetTotalNodes();

                    lock (progressStateLock)
                    {
                        progressState.CurrentDepth = depth;
                        progressState.Score = score;
                        progressState.BestMove = bestMove.ToString();
                    }

                    ReportProgress(false, depth, score, bestMove.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                // 回傳目前為止的最佳結果
            }
            finally
            {
                stopwatch.Stop();

                // 通知所有輔助 worker 停止
                linkedCts.Cancel();

                // 等待輔助 worker 與時間監控任務平順結束
                try { Task.WaitAll(helperTasks); }
                catch { /* Helper 可能拋出 OperationCanceledException，屬於預期狀況 */ }

                if (timeLimitMonitor != null)
                {
                    try { timeLimitMonitor.Wait(500); }
                    catch { }
                }

                if (heartbeatTimer != null)
                {
                    heartbeatTimer.Stop();
                    heartbeatTimer.Dispose();
                }
            }

            result.Nodes = GetTotalNodes();
            return result;
        }, ct);
    }

    public Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (asJson)
        {
            _tt.ExportToJson(output);
        }
        else
        {
            _tt.ExportToBinary(output);
        }

        return Task.CompletedTask;
    }

    public Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (asJson)
        {
            _tt.ImportFromJson(input);
        }
        else
        {
            _tt.ImportFromBinary(input);
        }

        return Task.CompletedTask;
    }
}
