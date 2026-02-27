using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Infrastructure.AI.Search;

public class SearchEngine : IAiEngine
{
    private readonly IEvaluator _evaluator;
    private readonly TranspositionTable _tt;

    private const int HeartbeatIntervalMs = 500;

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

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = linkedCts.Token;

            // --- 建立主 worker ---
            var mainWorker = new SearchWorker(board.Clone(), _evaluator, _tt, token);

            // --- 啟動輔助 worker（各自獨立執行迭代加深） ---
            var helperWorkers = new SearchWorker[threadCount - 1];
            var helperTasks = new Task[threadCount - 1];

            for (int i = 0; i < threadCount - 1; i++)
            {
                var helper = new SearchWorker(board.Clone(), _evaluator, _tt, token);
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
                long elapsedMs = stopwatch.ElapsedMilliseconds;
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
                    try { ReportProgress(true, 0, 0, null); }
                    catch { /* 忽略背景進度回報可忽略的失敗。 */ }
                };
                heartbeatTimer.Start();
                ReportProgress(true, 0, 0, null);
            }

            try
            {
                for (int depth = 1; depth <= settings.Depth; depth++)
                {
                    if (token.IsCancellationRequested) break;

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

                // 等待輔助 worker 平順結束
                try { Task.WaitAll(helperTasks); }
                catch { /* Helper 可能拋出 OperationCanceledException，屬於預期狀況 */ }

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
}
