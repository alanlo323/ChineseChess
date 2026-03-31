using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Helpers;
using ChineseChess.Infrastructure.AI.Evaluators;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Infrastructure.AI.Search;

public class SearchEngine : IAiEngine
{
    private readonly IEvaluator evaluator;
    private readonly TranspositionTable tt;
    private const int HeartbeatIntervalMs = 100;

    // Aspiration Window 相關常數
    private const int AspWindowDelta = 50;          // 初始窗口半寬
    private const int AspWindowExpansionFactor = 4; // 重試時窗口擴大倍數
    private const int AspWindowMaxRetries = 2;       // 最大重試次數（超過後回退全窗口）

    private sealed class SearchProgressState
    {
        public int CurrentDepth;
        public int Score;
        public string? BestMove;
        public int BestMoveFrom = -1;
        public int BestMoveTo = -1;
    }

    public SearchEngine(GameSettings settings)
        : this(settings, new HandcraftedEvaluator()) { }

    /// <summary>以注入的評估器建立引擎（供 NNUE / 複合評估器使用）。</summary>
    public SearchEngine(GameSettings settings, IEvaluator evaluator)
    {
        this.evaluator = evaluator;
        tt = new TranspositionTable(settings.TranspositionTableSizeMb);
    }

    // 測試用：使用預設設定
    public SearchEngine() : this(new GameSettings()) { }

    // 以既有 TT 建立引擎（CloneWithCopiedTT / CloneWithEmptyTT 專用），保留原始評估器
    private SearchEngine(IEvaluator evaluator, TranspositionTable tt)
    {
        this.evaluator = evaluator;
        this.tt = tt;
    }

    public string EngineLabel => evaluator.Label;

    public Task<SearchResult> SearchAsync(IBoard board, SearchSettings settings, CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
    {
        return Task.Run(() =>
        {
            int threadCount = Math.Clamp(settings.ThreadCount, 1, 128);
            tt.NewGeneration();
            tt.TryAutoResize(); // 碰撞率過高時自動擴容（僅在搜尋開始前呼叫，確保 thread-safe）
            // 若呼叫端未提供 PauseSignal，則本地建立並負責 Dispose
            ManualResetEventSlim? ownedPauseSignal = settings.PauseSignal == null
                ? new ManualResetEventSlim(true)
                : null;
            var pauseSignal = settings.PauseSignal ?? ownedPauseSignal!;

            // 用獨立的 timeLimitCts 管理思考時間，讓監控任務依「實際搜尋時間」取消
            // 暫停期間不計入 time limit，恢復後繼續剩餘思考時間
            using var timeLimitCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeLimitCts.Token);
            var token = linkedCts.Token;

            // --- 建立主 worker ---
            // 每個 worker 需要獨立的 evaluator 實例（NnueEvaluator 有 per-instance 累加器）
            // ct = 使用者明確停止 token（hardStopCt），token = 時間限制 + 使用者停止（合併）
            var mainWorker = new SearchWorker(board.Clone(), evaluator.CreateWorkerInstance(), tt, new EvalCache(), token, ct, pauseSignal);

            // --- 啟動輔助 worker（各自獨立執行迭代加深） ---
            var helperWorkers = new SearchWorker[threadCount - 1];
            var helperTasks = new Task[threadCount - 1];

            for (int i = 0; i < threadCount - 1; i++)
            {
                var helper = new SearchWorker(board.Clone(), evaluator.CreateWorkerInstance(), tt, new EvalCache(), token, ct, pauseSignal);
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

            void ReportProgress(bool isHeartbeat, int depth, int score, string? bestMove, int bestMoveFrom = -1, int bestMoveTo = -1)
            {
                if (progress == null) return;

                int reportDepth = depth;
                int reportScore = score;
                string? reportBestMove = bestMove;
                int reportBestMoveFrom = bestMoveFrom;
                int reportBestMoveTo = bestMoveTo;

                if (isHeartbeat)
                {
                    lock (progressStateLock)
                    {
                        reportDepth = progressState.CurrentDepth;
                        reportScore = progressState.Score;
                        reportBestMove = progressState.BestMove;
                        reportBestMoveFrom = progressState.BestMoveFrom;
                        reportBestMoveTo = progressState.BestMoveTo;
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
                    BestMoveFrom = reportBestMoveFrom,
                    BestMoveTo = reportBestMoveTo,
                    ElapsedMs = elapsedMs,
                    NodesPerSecond = nodesPerSecond,
                    IsHeartbeat = isHeartbeat,
                    ThreadCount = threadCount,
                    TtHitRate = tt.GetStatistics().HitRate,
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
            // 監控 HardTimeLimitMs（向下相容：若未設定則用 TimeLimitMs）
            int effectiveHardLimitMs = settings.EffectiveHardLimitMs;
            Task? timeLimitMonitor = null;
            if (effectiveHardLimitMs > 0)
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

                            if (GetActiveElapsedMs() >= effectiveHardLimitMs)
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
                int prevScore = 0;
                int prevPrevScore = 0; // 用於自適應 Aspiration Window delta 計算

                for (int depth = 1; depth <= settings.Depth; depth++)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    int score;

                    // Aspiration Window：depth=1 使用全窗口，depth>=2 使用縮小窗口
                    if (depth == 1)
                    {
                        score = mainWorker.SearchSingleDepth(depth, -SearchWorker.InfinityValue, SearchWorker.InfinityValue);
                    }
                    else
                    {
                        // 自適應 Aspiration Window：以前兩層分數差估算期望波動，
                        // 分數穩定時縮小窗口（加快搜尋），波動大時擴大（減少重搜）
                        int delta = Math.Clamp(Math.Abs(prevScore - prevPrevScore) + 25, 25, 100);
                        int alpha = prevScore - delta;
                        int beta = prevScore + delta;
                        int retries = 0;
                        score = prevScore; // 預設保留上一層結果
                        int lastCandidate = prevScore; // 記錄最後一次完成的搜尋結果

                        while (true)
                        {
                            if (token.IsCancellationRequested) { score = lastCandidate; break; }

                            int candidate = mainWorker.SearchSingleDepth(depth, alpha, beta);
                            lastCandidate = candidate;

                            if (candidate <= alpha)
                            {
                                // Fail-low：分數低於下界，擴大下界後重試
                                if (retries >= AspWindowMaxRetries)
                                {
                                    // 超過重試上限，回退全窗口
                                    score = mainWorker.SearchSingleDepth(depth, -SearchWorker.InfinityValue, SearchWorker.InfinityValue);
                                    break;
                                }
                                delta *= AspWindowExpansionFactor;
                                alpha = prevScore - delta;
                                retries++;
                            }
                            else if (candidate >= beta)
                            {
                                // Fail-high：分數高於上界，擴大上界後重試
                                if (retries >= AspWindowMaxRetries)
                                {
                                    // 超過重試上限，回退全窗口
                                    score = mainWorker.SearchSingleDepth(depth, -SearchWorker.InfinityValue, SearchWorker.InfinityValue);
                                    break;
                                }
                                delta *= AspWindowExpansionFactor;
                                beta = prevScore + delta;
                                retries++;
                            }
                            else
                            {
                                // 搜尋成功（分數在窗口內）
                                score = candidate;
                                break;
                            }
                        }
                    }

                    prevPrevScore = prevScore;
                    prevScore = score;

                    var bestMove = mainWorker.ProbeBestMove();

                    result.BestMove = bestMove;
                    result.Score = score;
                    result.Depth = depth;
                    result.Nodes = GetTotalNodes();
                    result.PvLine = BuildPrincipalVariation(board, depth);

                    var bestMoveNotation = MoveNotation.ToNotation(bestMove, board);
                    int bestMoveFromIdx = bestMove.IsNull ? -1 : bestMove.From;
                    int bestMoveToIdx = bestMove.IsNull ? -1 : bestMove.To;
                    lock (progressStateLock)
                    {
                        progressState.CurrentDepth = depth;
                        progressState.Score = score;
                        progressState.BestMove = bestMoveNotation;
                        progressState.BestMoveFrom = bestMoveFromIdx;
                        progressState.BestMoveTo = bestMoveToIdx;
                    }

                    ReportProgress(false, depth, score, bestMoveNotation, bestMoveFromIdx, bestMoveToIdx);

                    // Soft 時限檢查：每完成一整層後才檢查（不在重搜期間截斷）
                    if (settings.SoftTimeLimitMs.HasValue
                        && GetActiveElapsedMs() >= settings.SoftTimeLimitMs.Value)
                    {
                        break;
                    }
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

                ownedPauseSignal?.Dispose();
            }

            result.Nodes = GetTotalNodes();
            return result;
        }, ct);
    }

    private string BuildPrincipalVariation(IBoard rootBoard, int maxPly)
    {
        if (maxPly <= 0)
        {
            return string.Empty;
        }

        var pvBoard = rootBoard.Clone();
        var visited = new HashSet<ulong>();
        var pvMoves = new List<string>(maxPly);

        for (int ply = 0; ply < maxPly; ply++)
        {
            ulong key = pvBoard.ZobristKey;
            if (!visited.Add(key))
            {
                break;
            }

            if (!tt.Probe(key, out var entry) || entry.BestMove.IsNull)
            {
                break;
            }

            var legalMoves = pvBoard.GenerateLegalMoves();
            if (!legalMoves.Contains(entry.BestMove))
            {
                break;
            }

            pvMoves.Add(MoveNotation.ToNotation(entry.BestMove, pvBoard));
            pvBoard.MakeMove(entry.BestMove);
        }

        return string.Join(" ", pvMoves);
    }

    // 深度門檻：>= 此值時改用多執行緒（每個走法獨立 worker 平行跑）
    private const int ParallelDepthThreshold = 3;

    public Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesAsync(
        IBoard board, IEnumerable<Move> moves, int depth, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        var moveList = moves.ToList();
        int clampedDepth = Math.Max(1, depth);

        // 智能決策：淺層單執行緒（快），深層多執行緒（平行）
        return clampedDepth >= ParallelDepthThreshold
            ? EvaluateMovesParallelAsync(board, moveList, clampedDepth, ct, progress)
            : EvaluateMovesSequentialAsync(board, moveList, clampedDepth, ct, progress);
    }

    private Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesSequentialAsync(
        IBoard board, IReadOnlyList<Move> moves, int depth, CancellationToken ct, IProgress<string>? progress)
    {
        return Task.Run(() =>
        {
            using var noopPause = new ManualResetEventSlim(true);
            var worker = new SearchWorker(board.Clone(), evaluator.CreateWorkerInstance(), tt, new EvalCache(), ct, ct, noopPause);
            return worker.EvaluateRootMoves(moves, depth, progress, threadLabel: "單執行緒");
        }, ct);
    }

    private async Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesParallelAsync(
        IBoard board, IReadOnlyList<Move> moves, int depth, CancellationToken ct, IProgress<string>? progress)
    {
        int total = moves.Count;
        int threadCount = Math.Min(total, Environment.ProcessorCount);
        int completed = 0;
        int bestScore = int.MinValue;
        var scoreLock = new object();
        var results = new ConcurrentBag<(Move Move, int Score)>();

        string threadLabel = $"{threadCount} 執行緒";

        // 將走法分成 threadCount 個批次，每個批次由一個 LongRunning Task 處理，
        // 避免對每個走法各建一個短任務造成大量上下文切換
        int batchSize = (total + threadCount - 1) / threadCount;
        var batches = Enumerable.Range(0, threadCount)
            .Select(i => moves.Skip(i * batchSize).Take(batchSize).ToList())
            .Where(b => b.Count > 0)
            .ToArray();

        var tasks = batches.Select(batch => Task.Factory.StartNew(() =>
        {
            using var noopPause = new ManualResetEventSlim(true);
            foreach (var move in batch)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var clonedBoard = board.Clone();
                    clonedBoard.MakeMove(move);
                    var worker = new SearchWorker(clonedBoard, evaluator.CreateWorkerInstance(), tt, new EvalCache(), ct, ct, noopPause);
                    // 限制延伸深度：board 已走一步，SearchSingleDepth 從 ply=0 開始
                    // 延伸預算 +4：避免 check extension 在深層局面造成指數爆炸
                    worker.effectiveMaxPly = (depth - 1) + 4;
                    int score = -worker.SearchSingleDepth(depth - 1);

                    results.Add((move, score));

                    int done = Interlocked.Increment(ref completed);
                    int currentBest;
                    lock (scoreLock)
                    {
                        if (score > bestScore) bestScore = score;
                        currentBest = bestScore;
                    }

                    string bestStr = currentBest > 0 ? $"+{currentBest}" : currentBest.ToString();
                    progress?.Report($"智能提示分析中（深度 {depth}，{threadLabel}）：{done}/{total} 走法，目前最佳 {bestStr}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        await Task.WhenAll(tasks);

        var sorted = results.OrderByDescending(r => r.Score).ToList();
        return sorted.Select((r, i) => new MoveEvaluation
        {
            Move = r.Move,
            Score = r.Score,
            IsBest = i == 0
        }).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MoveEvaluation>> SearchMultiPvAsync(
        IBoard board, SearchSettings settings, int pvCount,
        CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
    {
        var allMoves = board.GenerateLegalMoves().ToList();
        if (allMoves.Count == 0) return [];

        int clampedCount = Math.Clamp(pvCount, 1, allMoves.Count);

        // 評估所有合法著法（共用 TT，各著法獨立搜尋後 TT 有足夠覆蓋）
        var evaluations = await EvaluateMovesAsync(board, allMoves, settings.Depth, ct);

        var result = new List<MoveEvaluation>(clampedCount);

        for (int i = 0; i < Math.Min(clampedCount, evaluations.Count); i++)
        {
            var eval = evaluations[i];

            // 從此著法出發，追蹤 TT 建立 PV 序列
            var pvBoard = board.Clone();
            pvBoard.MakeMove(eval.Move);
            string firstMoveStr = MoveNotation.ToNotation(eval.Move, board);
            string restPv = BuildPrincipalVariation(pvBoard, settings.Depth - 1);
            string pvLine = string.IsNullOrEmpty(restPv)
                ? firstMoveStr
                : firstMoveStr + " " + restPv;

            result.Add(new MoveEvaluation
            {
                Move = eval.Move,
                Score = eval.Score,
                IsBest = i == 0,
                PvLine = pvLine
            });
        }

        return result;
    }

    public Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // 使用 Task.Run 避免大型 TT 的同步 I/O（BrotliStream/BinaryWriter）阻塞呼叫端執行緒
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (asJson)
                tt.ExportToJson(output);
            else
                tt.ExportToBinary(output);
        }, ct);
    }

    public Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // 使用 Task.Run 避免大型 TT 的同步 I/O（BrotliStream/BinaryReader）阻塞呼叫端執行緒
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (asJson)
                tt.ImportFromJson(input);
            else
                tt.ImportFromBinary(input);
        }, ct);
    }

    public TTStatistics GetTTStatistics() => tt.GetStatistics();

    /// <inheritdoc/>
    public IAiEngine CloneWithCopiedTT() => new SearchEngine(evaluator, tt.Clone());

    /// <inheritdoc/>
    public IAiEngine CloneWithEmptyTT() => new SearchEngine(evaluator, new TranspositionTable(tt.GetStatistics().Capacity));

    /// <inheritdoc/>
    public void MergeTranspositionTableFrom(IAiEngine other)
    {
        if (other is SearchEngine otherEngine && !ReferenceEquals(this, otherEngine))
        {
            tt.MergeFrom(otherEngine.tt);
        }
    }

    /// <inheritdoc/>
    public IEnumerable<TTEntry> EnumerateTTEntries() => tt.EnumerateEntries();

    /// <inheritdoc/>
    public void StoreTTEntry(ulong key, int score, int depth, Move bestMove) =>
        tt.Store(key, score, Math.Clamp(depth, 0, 127), TTFlag.Exact, bestMove);

    /// <inheritdoc/>
    public TTTreeNode? ExploreTTTree(IBoard board, int maxDepth = 6) =>
        tt.ExploreTTTree(board, maxDepth);
}
