using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using System;
using System.Collections.Generic;
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
    private const int QuiescenceMaxPly = 8;
    private const int MaxSearchPly = 128;

    private static readonly int[] PieceValues = { 0, 10000, 120, 120, 270, 600, 285, 30 };
    private static readonly int[] FutilityMargins = { 0, 200, 500 };

    // Move ordering state — reset each search
    private Move[,] _killerMoves = new Move[MaxSearchPly, 2];
    private int[,] _historyTable = new int[90, 90];

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
            _nodesVisited = 0;
            _ct = ct;
            _tt.Clear();
            Array.Clear(_killerMoves, 0, _killerMoves.Length);
            Array.Clear(_historyTable, 0, _historyTable.Length);

            var result = new SearchResult();
            int currentDepth = 0;
            var stopwatch = Stopwatch.StartNew();
            var progressState = new SearchProgressState();
            var progressStateLock = new object();
            System.Timers.Timer? heartbeatTimer = null;

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
                    if (_ct.IsCancellationRequested) return;
                    try { ReportProgress(true, currentDepth, 0, null); }
                    catch { /* Ignore background progress report failures. */ }
                };
                heartbeatTimer.Start();
                ReportProgress(true, 0, 0, null);
            }

            try
            {
                // Iterative Deepening
                for (int depth = 1; depth <= settings.Depth; depth++)
                {
                    currentDepth = depth;
                    if (_ct.IsCancellationRequested) break;

                    int score = Negamax(board, depth, 0, -Infinity, Infinity, false);

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

    private int Negamax(IBoard board, int depth, int ply, int alpha, int beta, bool skipNullMove)
    {
        Interlocked.Increment(ref _nodesVisited);
        if (_nodesVisited % 2000 == 0 && _ct.IsCancellationRequested)
            throw new OperationCanceledException();

        bool isPvNode = (beta - alpha) > 1;

        // 1. TT Probe
        Move ttMove = Move.Null;
        if (_tt.Probe(board.ZobristKey, out var entry))
        {
            if (entry.Depth >= depth && !isPvNode)
            {
                if (entry.Flag == TTFlag.Exact) return entry.Score;
                if (entry.Flag == TTFlag.LowerBound) alpha = Math.Max(alpha, entry.Score);
                if (entry.Flag == TTFlag.UpperBound) beta = Math.Min(beta, entry.Score);
                if (alpha >= beta) return entry.Score;
            }
            ttMove = entry.BestMove;
        }

        // 2. Leaf node — drop into quiescence
        if (depth <= 0)
        {
            return Quiescence(board, alpha, beta, 0);
        }

        bool inCheck = board.IsCheck(board.Turn);

        // 3. Razor Pruning (depth == 3, not in check, not PV)
        if (depth == 3 && !inCheck && !isPvNode)
        {
            int staticEval = _evaluator.Evaluate(board);
            if (staticEval + 900 <= alpha)
            {
                int razorScore = Quiescence(board, alpha, beta, 0);
                if (razorScore <= alpha) return alpha;
            }
        }

        // 4. Null-Move Pruning
        if (depth >= 3 && !inCheck && !skipNullMove && HasSufficientMaterial(board))
        {
            int R = depth > 6 ? 3 : 2;
            board.MakeNullMove();
            int nullScore = -Negamax(board, depth - 1 - R, ply + 1, -beta, -beta + 1, true);
            board.UnmakeNullMove();
            if (nullScore >= beta) return beta;
        }

        // 5. Futility pruning flag
        bool futilityPruning = false;
        if (depth <= 2 && !inCheck && !isPvNode)
        {
            int staticEval = _evaluator.Evaluate(board);
            if (staticEval + FutilityMargins[depth] <= alpha)
            {
                futilityPruning = true;
            }
        }

        // 6. Generate & order moves
        var moves = board.GenerateLegalMoves().ToList();

        if (moves.Count == 0)
        {
            if (inCheck) return -MateScore + ply;
            return 0; // Stalemate
        }

        OrderMoves(board, moves, ttMove, ply);

        int bestScore = -Infinity;
        Move bestMove = Move.Null;
        TTFlag flag = TTFlag.UpperBound;

        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            bool isCapture = !board.GetPiece(move.To).IsNone;
            bool isKiller = IsKillerMove(ply, move);

            // 7. Futility pruning — skip quiet moves when hopeless
            if (futilityPruning && i > 0 && !isCapture && !isKiller)
            {
                continue;
            }

            board.MakeMove(move);

            // 8. Check Extension
            bool givesCheck = board.IsCheck(board.Turn);
            int extension = givesCheck ? 1 : 0;

            int score;

            // 9. Late Move Reductions (LMR)
            int reduction = 0;
            if (i >= 4 && depth >= 3 && !isCapture && !givesCheck && !isKiller && !inCheck)
            {
                reduction = 1;
                if (i >= 8) reduction = 2;
            }

            if (reduction > 0)
            {
                score = -Negamax(board, depth - 1 - reduction + extension, ply + 1, -beta, -alpha, false);
                // Re-search at full depth if reduced search improves alpha
                if (score > alpha)
                {
                    score = -Negamax(board, depth - 1 + extension, ply + 1, -beta, -alpha, false);
                }
            }
            else
            {
                score = -Negamax(board, depth - 1 + extension, ply + 1, -beta, -alpha, false);
            }

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
                        // Beta cutoff — update killer & history for quiet moves
                        if (!isCapture)
                        {
                            UpdateKillers(ply, move);
                            _historyTable[move.From, move.To] += depth * depth;
                        }

                        _tt.Store(board.ZobristKey, beta, depth, TTFlag.LowerBound, move);
                        return beta;
                    }
                }
            }
        }

        _tt.Store(board.ZobristKey, bestScore, depth, flag, bestMove);
        return bestScore;
    }

    private int Quiescence(IBoard board, int alpha, int beta, int ply)
    {
        Interlocked.Increment(ref _nodesVisited);

        int eval = _evaluator.Evaluate(board);
        if (eval >= beta) return beta;
        if (eval > alpha) alpha = eval;

        if (ply >= QuiescenceMaxPly) return alpha;

        var captures = board.GenerateLegalMoves()
            .Where(m => !board.GetPiece(m.To).IsNone)
            .ToList();

        if (captures.Count == 0) return alpha;

        OrderMoves(board, captures, Move.Null, 0);

        foreach (var move in captures)
        {
            if (_ct.IsCancellationRequested) throw new OperationCanceledException();

            board.MakeMove(move);
            int score = -Quiescence(board, -beta, -alpha, ply + 1);
            board.UnmakeMove(move);

            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }

        return alpha;
    }

    // --- Move Ordering ---

    private void OrderMoves(IBoard board, List<Move> moves, Move ttMove, int ply)
    {
        var scored = new int[moves.Count];
        for (int i = 0; i < moves.Count; i++)
        {
            scored[i] = ScoreMove(board, moves[i], ttMove, ply);
        }

        // Simple insertion sort — fast for typical move list sizes (< 80)
        for (int i = 1; i < moves.Count; i++)
        {
            var moveKey = moves[i];
            int scoreKey = scored[i];
            int j = i - 1;
            while (j >= 0 && scored[j] < scoreKey)
            {
                moves[j + 1] = moves[j];
                scored[j + 1] = scored[j];
                j--;
            }
            moves[j + 1] = moveKey;
            scored[j + 1] = scoreKey;
        }
    }

    private int ScoreMove(IBoard board, Move move, Move ttMove, int ply)
    {
        // 1. TT / Hash move
        if (move == ttMove) return 1_000_000;

        var movingPiece = board.GetPiece(move.From);
        var targetPiece = board.GetPiece(move.To);

        if (movingPiece.IsNone) return int.MinValue;

        // 2. Captures — MVV-LVA
        if (!targetPiece.IsNone)
        {
            int victimVal = PieceValues[(int)targetPiece.Type];
            int attackerVal = PieceValues[(int)movingPiece.Type];
            return 100_000 + victimVal * 10 - attackerVal;
        }

        // 3. Killer moves
        if (ply < MaxSearchPly)
        {
            if (move == _killerMoves[ply, 0]) return 90_000;
            if (move == _killerMoves[ply, 1]) return 89_000;
        }

        // 4. History heuristic
        return _historyTable[move.From, move.To];
    }

    private void UpdateKillers(int ply, Move move)
    {
        if (ply >= MaxSearchPly) return;

        if (move != _killerMoves[ply, 0])
        {
            _killerMoves[ply, 1] = _killerMoves[ply, 0];
            _killerMoves[ply, 0] = move;
        }
    }

    private bool IsKillerMove(int ply, Move move)
    {
        if (ply >= MaxSearchPly) return false;
        return move == _killerMoves[ply, 0] || move == _killerMoves[ply, 1];
    }

    // --- Helpers ---

    private static bool HasSufficientMaterial(IBoard board)
    {
        int attackers = 0;
        for (int i = 0; i < 90; i++)
        {
            var p = board.GetPiece(i);
            if (p.IsNone || p.Color != board.Turn) continue;
            if (p.Type == PieceType.Rook) return true;
            if (p.Type == PieceType.Cannon || p.Type == PieceType.Horse)
            {
                attackers++;
                if (attackers >= 2) return true;
            }
        }
        return false;
    }
}
