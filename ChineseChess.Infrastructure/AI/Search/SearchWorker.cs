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
/// 用於 Lazy SMP 架構的單一搜尋 worker。
/// 每個 worker 各自持有 IBoard 複本、killer/history 表與節點計數器。
/// TranspositionTable 與 IEvaluator 在所有 worker 間共用（thread-safe）。
/// </summary>
internal sealed class SearchWorker
{
    private readonly IBoard _board;
    private readonly IEvaluator _evaluator;
    private readonly TranspositionTable _tt;
    private readonly CancellationToken _ct;         // 時間限制 + 使用者停止（合併）
    private readonly CancellationToken _hardStopCt; // 僅使用者明確停止（暫停等待用）
    private readonly ManualResetEventSlim _pauseSignal;

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

    public SearchWorker(IBoard board, IEvaluator evaluator, TranspositionTable tt, CancellationToken ct, CancellationToken hardStopCt, ManualResetEventSlim pauseSignal)
    {
        _board = board;
        _evaluator = evaluator;
        _tt = tt;
        _ct = ct;
        _hardStopCt = hardStopCt;
        _pauseSignal = pauseSignal;
        _killerMoves = new Move[MaxSearchPly, 2];
        _historyTable = new int[90, 90];
    }

    /// <summary>
    /// 從深度 1 持續進行迭代加深到 targetDepth。
    /// 回傳在已完成的最深層深度中找到的最佳結果。
    /// </summary>
    public SearchResult Search(int targetDepth)
    {
        _nodesVisited = 0;
        Array.Clear(_killerMoves, 0, _killerMoves.Length);
        Array.Clear(_historyTable, 0, _historyTable.Length);
        CheckPauseOrCancellation();

        var result = new SearchResult();

        try
        {
            for (int depth = 1; depth <= targetDepth; depth++)
            {
                CheckPauseOrCancellation();
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
            // 回傳目前為止最佳結果
        }

        return result;
    }

    /// <summary>
    /// 搜尋單一深度並回傳分數，不執行迭代加深。
    /// 由主 worker 的外部協調器逐深度驅動時使用。
    /// </summary>
    public int SearchSingleDepth(int depth)
    {
        CheckPauseOrCancellation();
        return Negamax(depth, 0, -Infinity, Infinity, false);
    }

    public Move ProbeBestMove()
    {
        if (_tt.Probe(_board.ZobristKey, out var entry))
            return entry.BestMove;
        return Move.Null;
    }

    /// <summary>
    /// 對指定走法列表進行評分，回傳各走法的分數（從做出走法的一方視角，正分=有利）。
    /// 使用 Negamax 搜尋至指定深度（depth=1 表示只做靜態評估）。
    /// </summary>
    public IReadOnlyList<MoveEvaluation> EvaluateRootMoves(
        IEnumerable<Move> moves, int depth, IProgress<string>? progress = null, string threadLabel = "單執行緒")
    {
        _nodesVisited = 0;
        Array.Clear(_killerMoves, 0, _killerMoves.Length);
        Array.Clear(_historyTable, 0, _historyTable.Length);

        var moveList = moves.ToList();
        var results = new List<(Move Move, int Score)>();
        int searchDepth = Math.Max(0, depth - 1);
        int total = moveList.Count;
        int bestScore = -Infinity;

        for (int i = 0; i < total; i++)
        {
            if (_ct.IsCancellationRequested) break;

            var move = moveList[i];
            try
            {
                _board.MakeMove(move);
                // Negamax 在 MakeMove 後已切換行棋方，取負號還原為「做出此走法的玩家」視角
                int score = -Negamax(searchDepth, 1, -Infinity, Infinity, false);
                _board.UnmakeMove(move);

                results.Add((move, score));
                if (score > bestScore) bestScore = score;

                string bestStr = bestScore > 0 ? $"+{bestScore}" : bestScore.ToString();
                progress?.Report($"智能提示分析中（深度 {depth}，{threadLabel}）：{i + 1}/{total} 走法，目前最佳 {bestStr}");
            }
            catch (OperationCanceledException)
            {
                // MakeMove 已執行，Negamax 中取消 → 還原棋盤後停止
                _board.UnmakeMove(move);
                break;
            }
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));

        return results.Select((r, idx) => new MoveEvaluation
        {
            Move = r.Move,
            Score = r.Score,
            IsBest = idx == 0
        }).ToList();
    }

    // --- 核心搜尋流程 ---

    private int Negamax(int depth, int ply, int alpha, int beta, bool skipNullMove)
    {
        CheckPauseOrCancellation();
        Interlocked.Increment(ref _nodesVisited);
        if (_nodesVisited % 2000 == 0)
            CheckPauseOrCancellation();

        bool isPvNode = (beta - alpha) > 1;

        // 1. TT 探測
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

        // 2. 重覆局面偵測（搜尋中用 2 次閾值視為和棋，回傳 0）
        if (ply > 0 && _board.IsDrawByRepetition(threshold: 2))
        {
            return 0;
        }

        // 3. 葉節點：進入 quiescence
        if (depth <= 0)
        {
            return Quiescence(alpha, beta, 0);
        }

        bool inCheck = _board.IsCheck(_board.Turn);

        // 3. Razor 剪枝（depth == 3，且不在將軍/非 PV）
        if (depth == 3 && !inCheck && !isPvNode)
        {
            int staticEval = _evaluator.Evaluate(_board);
            if (staticEval + 900 <= alpha)
            {
                int razorScore = Quiescence(alpha, beta, 0);
                if (razorScore <= alpha) return alpha;
            }
        }

        // 4. Null-Move 剪枝
        if (depth >= 3 && !inCheck && !skipNullMove && HasSufficientMaterial())
        {
            int R = depth > 6 ? 3 : 2;
            _board.MakeNullMove();
            int nullScore = -Negamax(depth - 1 - R, ply + 1, -beta, -beta + 1, true);
            _board.UnmakeNullMove();
            if (nullScore >= beta) return beta;
        }

        // 5. Futility 剪枝旗標
        bool futilityPruning = false;
        if (depth <= 2 && !inCheck && !isPvNode)
        {
            int staticEval = _evaluator.Evaluate(_board);
            if (staticEval + FutilityMargins[depth] <= alpha)
            {
                futilityPruning = true;
            }
        }

        // 6. 產生並排序著法
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

            // 7. Futility 剪枝：若無望則略過靜態著法
            if (futilityPruning && i > 0 && !isCapture && !isKiller)
            {
                continue;
            }

            _board.MakeMove(move);

            // 8. 將軍延伸（Check Extension）
            bool givesCheck = _board.IsCheck(_board.Turn);
            int extension = givesCheck ? 1 : 0;

            int score;

            // 9. 後序著法減枝（LMR）
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
        CheckPauseOrCancellation();
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
            CheckPauseOrCancellation();

            _board.MakeMove(move);
            int score = -Quiescence(-beta, -alpha, ply + 1);
            _board.UnmakeMove(move);

            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }

        return alpha;
    }

    // --- 著法排序 ---

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

    // --- 輔助邏輯 ---

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

    private void CheckPauseOrCancellation()
    {
        // 使用 _hardStopCt（僅使用者明確停止）等待，避免時間限制到期時破壞暫停狀態
        // 時間限制（_ct）到期不應中斷暫停；只有明確 Stop 才能跳出
        _pauseSignal.Wait(_hardStopCt);
        if (_ct.IsCancellationRequested || _hardStopCt.IsCancellationRequested)
            throw new OperationCanceledException();
    }
}
