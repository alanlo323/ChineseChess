using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ChineseChess.Infrastructure.AI.Search;

/// <summary>
/// A single search worker for the Lazy SMP scheme.
/// Each worker owns its own IBoard copy, killer/history tables, and node counter.
/// The TranspositionTable and IEvaluator are shared (thread-safe) across all workers.
/// </summary>
internal sealed class SearchWorker
{
    private readonly IBoard _board;
    private readonly IEvaluator _evaluator;
    private readonly TranspositionTable _tt;
    private readonly CancellationToken _ct;

    private long _nodesVisited;
    private Move[,] _killerMoves;
    private int[,] _historyTable;

    private const int Infinity = 30000;
    private const int MateScore = 20000;
    private const int QuiescenceMaxPly = 8;
    private const int MaxSearchPly = 128;

    private static readonly int[] PieceValues = { 0, 10000, 120, 120, 270, 600, 285, 30 };
    private static readonly int[] FutilityMargins = { 0, 200, 500 };

    public long NodesVisited => Interlocked.Read(ref _nodesVisited);

    public SearchWorker(IBoard board, IEvaluator evaluator, TranspositionTable tt, CancellationToken ct)
    {
        _board = board;
        _evaluator = evaluator;
        _tt = tt;
        _ct = ct;
        _killerMoves = new Move[MaxSearchPly, 2];
        _historyTable = new int[90, 90];
    }

    /// <summary>
    /// Run iterative deepening from depth 1 up to targetDepth.
    /// Returns the best result found at the deepest completed depth.
    /// </summary>
    public SearchResult Search(int targetDepth)
    {
        _nodesVisited = 0;
        Array.Clear(_killerMoves, 0, _killerMoves.Length);
        Array.Clear(_historyTable, 0, _historyTable.Length);

        var result = new SearchResult();

        try
        {
            for (int depth = 1; depth <= targetDepth; depth++)
            {
                if (_ct.IsCancellationRequested) break;

                int score = Negamax(depth, 0, -Infinity, Infinity, false);

                var bestMove = Move.Null;
                if (_tt.Probe(_board.ZobristKey, out var entry))
                {
                    bestMove = entry.BestMove;
                }

                result.BestMove = bestMove;
                result.Score = score;
                result.Depth = depth;
                result.Nodes = Interlocked.Read(ref _nodesVisited);
            }
        }
        catch (OperationCanceledException)
        {
            // Return best result so far
        }

        return result;
    }

    /// <summary>
    /// Search a single depth and return the score. Does not run iterative deepening.
    /// Used by the coordinator to drive per-depth iteration for the main worker.
    /// </summary>
    public int SearchSingleDepth(int depth)
    {
        return Negamax(depth, 0, -Infinity, Infinity, false);
    }

    public Move ProbeBestMove()
    {
        if (_tt.Probe(_board.ZobristKey, out var entry))
            return entry.BestMove;
        return Move.Null;
    }

    // --- Core Search ---

    private int Negamax(int depth, int ply, int alpha, int beta, bool skipNullMove)
    {
        Interlocked.Increment(ref _nodesVisited);
        if (_nodesVisited % 2000 == 0 && _ct.IsCancellationRequested)
            throw new OperationCanceledException();

        bool isPvNode = (beta - alpha) > 1;

        // 1. TT Probe
        Move ttMove = Move.Null;
        if (_tt.Probe(_board.ZobristKey, out var entry))
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
            return Quiescence(alpha, beta, 0);
        }

        bool inCheck = _board.IsCheck(_board.Turn);

        // 3. Razor Pruning (depth == 3, not in check, not PV)
        if (depth == 3 && !inCheck && !isPvNode)
        {
            int staticEval = _evaluator.Evaluate(_board);
            if (staticEval + 900 <= alpha)
            {
                int razorScore = Quiescence(alpha, beta, 0);
                if (razorScore <= alpha) return alpha;
            }
        }

        // 4. Null-Move Pruning
        if (depth >= 3 && !inCheck && !skipNullMove && HasSufficientMaterial())
        {
            int R = depth > 6 ? 3 : 2;
            _board.MakeNullMove();
            int nullScore = -Negamax(depth - 1 - R, ply + 1, -beta, -beta + 1, true);
            _board.UnmakeNullMove();
            if (nullScore >= beta) return beta;
        }

        // 5. Futility pruning flag
        bool futilityPruning = false;
        if (depth <= 2 && !inCheck && !isPvNode)
        {
            int staticEval = _evaluator.Evaluate(_board);
            if (staticEval + FutilityMargins[depth] <= alpha)
            {
                futilityPruning = true;
            }
        }

        // 6. Generate & order moves
        var moves = _board.GenerateLegalMoves().ToList();

        if (moves.Count == 0)
        {
            if (inCheck) return -MateScore + ply;
            return 0;
        }

        OrderMoves(moves, ttMove, ply);

        int bestScore = -Infinity;
        Move bestMove = Move.Null;
        TTFlag flag = TTFlag.UpperBound;

        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            bool isCapture = !_board.GetPiece(move.To).IsNone;
            bool isKiller = IsKillerMove(ply, move);

            // 7. Futility pruning — skip quiet moves when hopeless
            if (futilityPruning && i > 0 && !isCapture && !isKiller)
            {
                continue;
            }

            _board.MakeMove(move);

            // 8. Check Extension
            bool givesCheck = _board.IsCheck(_board.Turn);
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
                score = -Negamax(depth - 1 - reduction + extension, ply + 1, -beta, -alpha, false);
                if (score > alpha)
                {
                    score = -Negamax(depth - 1 + extension, ply + 1, -beta, -alpha, false);
                }
            }
            else
            {
                score = -Negamax(depth - 1 + extension, ply + 1, -beta, -alpha, false);
            }

            _board.UnmakeMove(move);

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
                        if (!isCapture)
                        {
                            UpdateKillers(ply, move);
                            _historyTable[move.From, move.To] += depth * depth;
                        }

                        _tt.Store(_board.ZobristKey, beta, depth, TTFlag.LowerBound, move);
                        return beta;
                    }
                }
            }
        }

        _tt.Store(_board.ZobristKey, bestScore, depth, flag, bestMove);
        return bestScore;
    }

    private int Quiescence(int alpha, int beta, int ply)
    {
        Interlocked.Increment(ref _nodesVisited);

        int eval = _evaluator.Evaluate(_board);
        if (eval >= beta) return beta;
        if (eval > alpha) alpha = eval;

        if (ply >= QuiescenceMaxPly) return alpha;

        var captures = _board.GenerateLegalMoves()
            .Where(m => !_board.GetPiece(m.To).IsNone)
            .ToList();

        if (captures.Count == 0) return alpha;

        OrderMoves(captures, Move.Null, 0);

        foreach (var move in captures)
        {
            if (_ct.IsCancellationRequested) throw new OperationCanceledException();

            _board.MakeMove(move);
            int score = -Quiescence(-beta, -alpha, ply + 1);
            _board.UnmakeMove(move);

            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }

        return alpha;
    }

    // --- Move Ordering ---

    private void OrderMoves(List<Move> moves, Move ttMove, int ply)
    {
        var scored = new int[moves.Count];
        for (int i = 0; i < moves.Count; i++)
        {
            scored[i] = ScoreMove(moves[i], ttMove, ply);
        }

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

    private int ScoreMove(Move move, Move ttMove, int ply)
    {
        if (move == ttMove) return 1_000_000;

        var movingPiece = _board.GetPiece(move.From);
        var targetPiece = _board.GetPiece(move.To);

        if (movingPiece.IsNone) return int.MinValue;

        if (!targetPiece.IsNone)
        {
            int victimVal = PieceValues[(int)targetPiece.Type];
            int attackerVal = PieceValues[(int)movingPiece.Type];
            return 100_000 + victimVal * 10 - attackerVal;
        }

        if (ply < MaxSearchPly)
        {
            if (move == _killerMoves[ply, 0]) return 90_000;
            if (move == _killerMoves[ply, 1]) return 89_000;
        }

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

    private bool HasSufficientMaterial()
    {
        int attackers = 0;
        for (int i = 0; i < 90; i++)
        {
            var p = _board.GetPiece(i);
            if (p.IsNone || p.Color != _board.Turn) continue;
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
