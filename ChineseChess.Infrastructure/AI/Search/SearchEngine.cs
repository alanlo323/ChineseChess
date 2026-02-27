using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Infrastructure.AI.Search;

public class SearchEngine : IAiEngine
{
    private readonly IEvaluator _evaluator;
    private readonly TranspositionTable _tt;
    private long _nodesVisited;
    private CancellationToken _ct;
    private const int Infinity = 30000;
    private const int MateScore = 20000;
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
        _tt = new TranspositionTable(64); // 64MB default
    }

    public Task<SearchResult> SearchAsync(IBoard board, SearchSettings settings, CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
    {
        return Task.Run(() =>
        {
            _nodesVisited = 0;
            _ct = ct;
            _tt.Clear(); // Optional: Keep TT between moves? Usually yes, but for now clear to avoid stale data issues.

            var result = new SearchResult();
            int currentDepth = 0;
            var stopwatch = Stopwatch.StartNew();
            var progressState = new SearchProgressState();
            var progressStateLock = new object();
            System.Timers.Timer? heartbeatTimer = null;

            void ReportProgress(bool isHeartbeat, int depth, int score, string? bestMove)
            {
                if (progress == null)
                {
                    return;
                }

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

                long nodes = Interlocked.Read(ref _nodesVisited);
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
                    Message = isHeartbeat ? "Heartbeat report" : $"Depth {depth}/{settings.Depth} computed"
                });
            }

            if (progress != null)
            {
                heartbeatTimer = new System.Timers.Timer(HeartbeatIntervalMs);
                heartbeatTimer.Elapsed += (_, _) =>
                {
                    if (_ct.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        ReportProgress(true, currentDepth, 0, null);
                    }
                    catch
                    {
                        // Ignore background progress report failures.
                    }
                };
                heartbeatTimer.Start();

                ReportProgress(true, 0, 0, null);
            }

            try
            {
                for (int depth = 1; depth <= settings.Depth; depth++)
                {
                    currentDepth = depth;
                    if (_ct.IsCancellationRequested) break;

                    int score = Negamax(board, depth, 0, -Infinity, Infinity);

                    // Get PV from TT
                    var bestMove = Move.Null;
                    if (_tt.Probe(board.ZobristKey, out var entry))
                    {
                        bestMove = entry.BestMove;
                    }

                    result.BestMove = bestMove;
                    result.Score = score;
                    result.Depth = depth;
                    result.Nodes = Interlocked.Read(ref _nodesVisited);

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
                // Return best result so far
            }
            finally
            {
                stopwatch.Stop();
                if (heartbeatTimer != null)
                {
                    heartbeatTimer.Stop();
                    heartbeatTimer.Dispose();
                }
            }

            return result;
        }, ct);
    }

    private int Negamax(IBoard board, int depth, int ply, int alpha, int beta)
    {
        Interlocked.Increment(ref _nodesVisited);
        if (_nodesVisited % 2000 == 0 && _ct.IsCancellationRequested) throw new OperationCanceledException();

        // 1. TT Probe
        Move ttMove = Move.Null;
        if (_tt.Probe(board.ZobristKey, out var entry))
        {
            if (entry.Depth >= depth)
            {
                if (entry.Flag == TTFlag.Exact) return entry.Score;
                if (entry.Flag == TTFlag.LowerBound) alpha = Math.Max(alpha, entry.Score);
                if (entry.Flag == TTFlag.UpperBound) beta = Math.Min(beta, entry.Score);
                if (alpha >= beta) return entry.Score;
            }
            ttMove = entry.BestMove;
        }

        if (depth <= 0)
        {
            return Quiescence(board, alpha, beta);
        }

        // 2. Null Move Pruning (TODO: Condition check - not in Check, enough material)
        // if (depth >= 3 && !IsPv && !InCheck) { ... }

        // 3. Generate Moves
        // var moves = board.GenerateLegalMoves().ToList(); // Optimized: MoveGen(ttMove, captures...)
        
        // For demonstration, using simple list
        var moves = board.GenerateLegalMoves().ToList(); 
        
        if (moves.Count == 0)
        {
            if (board.IsCheck(board.Turn)) return -MateScore + ply;
            return 0; // Stalemate
        }

        // 4. Move Ordering
        // Order: TT Move, Captures (MVV-LVA), Killer, History...
        moves = moves.OrderByDescending(m => m == ttMove ? 100000 : 0).ToList();

        int bestScore = -Infinity;
        Move bestMove = Move.Null;
        TTFlag flag = TTFlag.UpperBound;

        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            
            // 5. LMR (Late Move Reduction)
            // if (i > 4 && depth > 2 && !IsCapture(move) && !IsCheck(move)) { ... }

            board.MakeMove(move);
            int score = -Negamax(board, depth - 1, ply + 1, -beta, -alpha);
            board.UnmakeMove(move);

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
                if (score > alpha)
                {
                    alpha = score;
                    flag = TTFlag.Exact;
                    if (alpha >= beta)
                    {
                        // Beta Cutoff
                        _tt.Store(board.ZobristKey, beta, depth, TTFlag.LowerBound, move);
                        return beta; 
                    }
                }
            }
        }

        _tt.Store(board.ZobristKey, bestScore, depth, flag, bestMove);
        return bestScore;
    }

    private int Quiescence(IBoard board, int alpha, int beta)
    {
        Interlocked.Increment(ref _nodesVisited);
        
        // Stand-pat
        int eval = _evaluator.Evaluate(board);
        if (eval >= beta) return beta;
        if (eval > alpha) alpha = eval;

        // Generate Captures Only
        // var captures = board.GenerateCaptures();
        var captures = board.GenerateLegalMoves(); // Should filter captures

        foreach (var move in captures)
        {
            // SEE (Static Exchange Evaluation) pruning could go here
            
            board.MakeMove(move);
            int score = -Quiescence(board, -beta, -alpha);
            board.UnmakeMove(move);

            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }

        return alpha;
    }
}
