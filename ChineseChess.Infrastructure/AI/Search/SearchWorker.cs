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
    private readonly IBoard board;
    private readonly IEvaluator evaluator;
    private readonly TranspositionTable tt;
    private readonly CancellationToken ct;         // 時間限制 + 使用者停止（合併）
    private readonly CancellationToken hardStopCt; // 僅使用者明確停止（暫停等待用）
    private readonly ManualResetEventSlim pauseSignal;

    private long nodesVisited;
    private Move[,] killerMoves;
    private int[,] historyTable;
    // H2b：反制著法表（countermoveTable[opponentFrom, opponentTo] = bestCounterMove）
    private Move[,] countermoveTable;
    // H1：Triangular PV Table（pvTable[ply, plyOffset] 存放 PV 著法，pvLength[ply] 存放長度）
    private readonly Move[,] pvTable;
    private readonly int[] pvLength;
    // 每次搜尋前可動態調整，避免 check extension 在淺層搜尋中無限延伸
    internal int effectiveMaxPly = MaxSearchPly;

    private const int Infinity = 30000;
    // 公開 Infinity 值，供 SearchEngine 使用（Aspiration Window 回退全窗口）
    internal const int InfinityValue = Infinity;
    // Lazy Evaluation：EvaluateFast 與 Evaluate 的允許差距邊際
    private const int LazyMargin = 200;
    private const int MateScore = 20000;
    private const int QuiescenceMaxPly = 8;
    private const int MaxSearchPly = 128;
    private const int CheckMoveBonus = 95_000;
    private const int SafeCaptureBonus = 1_500;
    private const int GuardedCapturePenaltyMultiplier = 12;
    private const int HighValueTacticalThreshold = 270;
    // IIR：Internal Iterative Reduction 相關常數
    private const int IirMinDepth = 4;                  // 觸發 IIR 的最小搜尋深度
    // SE：奇異延伸相關常數
    private const int SingularExtensionMinDepth = 6;   // 觸發 SE 的最小搜尋深度
    private const int SingularMargin = 2;               // 排除搜尋的分數邊際（sBeta = ttScore - SingularMargin * depth）
    private const int CheckmateThreshold = 15000;       // 將殺分數門檻：|ttScore| >= 此值時不觸發 SE

    private static readonly int[] PieceValues = { 0, 10000, 120, 120, 270, 600, 285, 30 };
    private static readonly int[] FutilityMargins = { 0, 200, 500 };

    public long NodesVisited => Interlocked.Read(ref nodesVisited);

    public SearchWorker(IBoard board, IEvaluator evaluator, TranspositionTable tt, CancellationToken ct, CancellationToken hardStopCt, ManualResetEventSlim pauseSignal)
    {
        this.board = board;
        this.evaluator = evaluator;
        this.tt = tt;
        this.ct = ct;
        this.hardStopCt = hardStopCt;
        this.pauseSignal = pauseSignal;
        killerMoves = new Move[MaxSearchPly, 2];
        historyTable = new int[90, 90];
        countermoveTable = new Move[90, 90];
        pvTable = new Move[MaxSearchPly, MaxSearchPly];
        pvLength = new int[MaxSearchPly];
    }

    /// <summary>
    /// 從深度 1 持續進行迭代加深到 targetDepth。
    /// 回傳在已完成的最深層深度中找到的最佳結果。
    /// </summary>
    public SearchResult Search(int targetDepth)
    {
        nodesVisited = 0;
        Array.Clear(killerMoves, 0, killerMoves.Length);
        Array.Clear(historyTable, 0, historyTable.Length);
        Array.Clear(pvLength, 0, pvLength.Length);
        CheckPauseOrCancellation();

        var result = new SearchResult();

        try
        {
            int prevScore = 0;
            const int helperAspDelta = 50;
            const int helperAspExpansion = 4;
            const int helperAspMaxRetries = 2;

            for (int depth = 1; depth <= targetDepth; depth++)
            {
                CheckPauseOrCancellation();
                if (ct.IsCancellationRequested) break;

                // H2a：每輪迭代加深開始前衰減歷史表，讓舊資訊逐漸淡出
                if (depth > 1) DecayHistory();

                int score;

                // Aspiration Window：depth=1 全窗口，depth>=2 縮小窗口
                if (depth == 1)
                {
                    score = Negamax(depth, 0, -Infinity, Infinity, false);
                }
                else
                {
                    int delta = helperAspDelta;
                    int alpha = prevScore - delta;
                    int beta = prevScore + delta;
                    int retries = 0;
                    score = prevScore;

                    while (true)
                    {
                        if (ct.IsCancellationRequested) break;
                        int candidate = Negamax(depth, 0, alpha, beta, false);

                        if (candidate <= alpha)
                        {
                            if (retries >= helperAspMaxRetries)
                            {
                                score = Negamax(depth, 0, -Infinity, Infinity, false);
                                break;
                            }
                            delta *= helperAspExpansion;
                            alpha = prevScore - delta;
                            retries++;
                        }
                        else if (candidate >= beta)
                        {
                            if (retries >= helperAspMaxRetries)
                            {
                                score = Negamax(depth, 0, -Infinity, Infinity, false);
                                break;
                            }
                            delta *= helperAspExpansion;
                            beta = prevScore + delta;
                            retries++;
                        }
                        else
                        {
                            score = candidate;
                            break;
                        }
                    }
                }

                prevScore = score;

                var bestMove = Move.Null;
                if (tt.Probe(board.ZobristKey, out var entry))
                {
                    bestMove = entry.BestMove;
                }

                result.BestMove = bestMove;
                result.Score = score;
                result.Depth = depth;
                result.Nodes = Interlocked.Read(ref nodesVisited);
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
        => SearchSingleDepth(depth, -Infinity, Infinity);

    /// <summary>
    /// 搜尋單一深度，接受自訂 alpha/beta 窗口（用於 Aspiration Window）。
    /// </summary>
    public int SearchSingleDepth(int depth, int alpha, int beta)
    {
        CheckPauseOrCancellation();
        return Negamax(depth, 0, alpha, beta, false);
    }

    public Move ProbeBestMove()
    {
        if (tt.Probe(board.ZobristKey, out var entry))
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
        nodesVisited = 0;
        Array.Clear(killerMoves, 0, killerMoves.Length);
        Array.Clear(historyTable, 0, historyTable.Length);

        var moveList = moves.ToList();
        var results = new List<(Move Move, int Score)>();
        int searchDepth = Math.Max(0, depth - 1);
        // 限制 check/capture extension 的總 ply，防止淺層搜尋無限延伸
        // ply 從 1 開始，延伸預算 +4：足以偵測4步以內的戰術威脅，不會指數爆炸
        effectiveMaxPly = searchDepth + 4;
        int total = moveList.Count;
        int bestScore = -Infinity;

        for (int i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested) break;

            var move = moveList[i];
            try
            {
                board.MakeMove(move);
                // Negamax 在 MakeMove 後已切換行棋方，取負號還原為「做出此走法的玩家」視角
                int score = -Negamax(searchDepth, 1, -Infinity, Infinity, false);
                board.UnmakeMove(move);

                results.Add((move, score));
                if (score > bestScore) bestScore = score;

                string bestStr = bestScore > 0 ? $"+{bestScore}" : bestScore.ToString();
                progress?.Report($"智能提示分析中（深度 {depth}，{threadLabel}）：{i + 1}/{total} 走法，目前最佳 {bestStr}");
            }
            catch (OperationCanceledException)
            {
                // MakeMove 已執行，Negamax 中取消 → 還原棋盤後停止
                board.UnmakeMove(move);
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

    private int Negamax(int depth, int ply, int alpha, int beta, bool skipNullMove,
        int opponentLastFrom = -1, int opponentLastTo = -1, Move excludedMove = default)
    {
        CheckPauseOrCancellation();
        Interlocked.Increment(ref nodesVisited);
        if (nodesVisited % 2000 == 0)
            CheckPauseOrCancellation();

        bool isPvNode = (beta - alpha) > 1;

        // 1. TT 探測
        Move ttMove = Move.Null;
        int ttScore = 0;
        int ttDepth = 0;
        TTFlag ttFlag = TTFlag.UpperBound;
        if (tt.Probe(board.ZobristKey, out var entry))
        {
            if (entry.Depth >= depth && !isPvNode)
            {
                if (entry.Flag == TTFlag.Exact) return entry.Score;
                if (entry.Flag == TTFlag.LowerBound) alpha = Math.Max(alpha, entry.Score);
                if (entry.Flag == TTFlag.UpperBound) beta = Math.Min(beta, entry.Score);
                if (alpha >= beta) return entry.Score;
            }
            ttMove = entry.BestMove;
            ttScore = entry.Score;
            ttDepth = entry.Depth;
            ttFlag = entry.Flag;
        }

        // 1b. IIR（Internal Iterative Reduction）：
        // TT 無命中 + depth 達門檻 + 非排除搜尋節點 → depth 減一，節省搜尋成本
        if (ttMove.IsNull && depth >= IirMinDepth && excludedMove.IsNull)
        {
            depth -= 1;
        }

        // 2. 和局早返（重覆局面 OR 無吃子超限）
        if (ply > 0 && (board.IsDrawByRepetition(threshold: 2) || board.IsDrawByNoCapture()))
        {
            return 0;
        }

        // 3. 葉節點：進入 quiescence（或超過有效最大 ply，避免將軍延伸無限遞迴）
        if (depth <= 0 || ply >= effectiveMaxPly)
        {
            return Quiescence(alpha, beta, 0);
        }

        bool inCheck = board.IsCheck(board.Turn);

        // 3. Razor 剪枝（depth == 3，且不在將軍/非 PV）
        // Lazy Evaluation：先以 EvaluateFast 預篩，再視需要呼叫完整 Evaluate
        if (depth == 3 && !inCheck && !isPvNode)
        {
            int fastEval = evaluator.EvaluateFast(board);
            if (fastEval + 900 > alpha)
            {
                // 快速評估接近或超過 alpha，不剪枝（EvaluateFast 可能低估）
            }
            else
            {
                // EvaluateFast 遠低於 alpha，呼叫完整評估確認
                int staticEval = evaluator.Evaluate(board);
                if (staticEval + 900 <= alpha)
                {
                    int razorScore = Quiescence(alpha, beta, 0);
                    if (razorScore <= alpha) return alpha;
                }
            }
        }

        // 4. Null-Move 剪枝
        if (depth >= 3 && !inCheck && !skipNullMove && HasSufficientMaterial())
        {
            int R = depth > 6 ? 3 : 2;
            board.MakeNullMove();
            int nullScore = -Negamax(depth - 1 - R, ply + 1, -beta, -beta + 1, true);
            board.UnmakeNullMove();
            if (nullScore >= beta) return beta;
        }

        // 5. Futility 剪枝旗標
        // Lazy Evaluation：先以 EvaluateFast 預篩，再視需要呼叫完整 Evaluate
        bool futilityPruning = false;
        if (depth <= 2 && !inCheck && !isPvNode)
        {
            int margin = FutilityMargins[depth];
            int fastEval = evaluator.EvaluateFast(board);

            if (fastEval + margin > alpha + LazyMargin)
            {
                // 快速評估明顯超過 alpha + LazyMargin，不剪枝（不需要呼叫完整評估）
            }
            else if (fastEval + margin < alpha - LazyMargin)
            {
                // 快速評估明顯低於 alpha - LazyMargin，直接剪枝（不需要呼叫完整評估）
                futilityPruning = true;
            }
            else
            {
                // 在邊界附近，呼叫完整評估判定
                int staticEval = evaluator.Evaluate(board);
                if (staticEval + margin <= alpha)
                {
                    futilityPruning = true;
                }
            }
        }

        // 6. 奇異延伸判斷（Singular Extension）
        // 條件：深度達到門檻、TT 有有效走法、TT 深度充足、TT 分數不在將殺範圍、
        //       ply > 0（根節點不做 SE）、目前不是排除搜尋節點（excludedMove 為空）
        bool singularExtension = false;
        if (depth >= SingularExtensionMinDepth
            && !ttMove.IsNull
            && ttDepth >= depth - 3
            && (ttFlag == TTFlag.LowerBound || ttFlag == TTFlag.Exact)
            && Math.Abs(ttScore) < CheckmateThreshold
            && ply > 0
            && excludedMove.IsNull)
        {
            // 排除搜尋：以略低於 TT 分數的視窗，在跳過 ttMove 的情況下搜尋
            int sBeta = ttScore - SingularMargin * depth;
            int reducedDepth = (depth - 1) / 2;
            int seScore = Negamax(reducedDepth, ply, sBeta - 1, sBeta, skipNullMove: true,
                opponentLastFrom, opponentLastTo, excludedMove: ttMove);
            // 若排除 TT 走法後分數低於 sBeta，代表 TT 走法明顯優於其他選擇 → 觸發奇異延伸
            if (seScore < sBeta)
            {
                singularExtension = true;
            }
        }

        // 7. 產生並排序著法
        var moves = board.GenerateLegalMoves().ToList();

        if (moves.Count == 0)
        {
            if (inCheck) return -MateScore + ply;
            return 0;
        }

        OrderMoves(moves, ttMove, ply, opponentLastFrom, opponentLastTo);

        int bestScore = -Infinity;
        Move bestMove = Move.Null;
        TTFlag flag = TTFlag.UpperBound;

        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];

            // 排除搜尋：跳過指定的排除走法（避免重複計算 TT 走法）
            if (!excludedMove.IsNull && move == excludedMove) continue;

            var movingPiece = board.GetPiece(move.From);
            var targetPiece = board.GetPiece(move.To);
            bool isCapture = !targetPiece.IsNone;
            bool isKiller = IsKillerMove(ply, move);
            // M1b：威脅延伸放寬至 ply <= 2（原 ply == 0），depth >= 2，i < 8（原 i < 4）
            bool threatCandidate = !isCapture
                && ply <= 2
                && depth >= 2
                && depth <= 3
                && i < 8;
            bool hadImmediateThreat = threatCandidate && SideToMoveHasHighValueCapture();

            // 7. Futility 剪枝：若無望則略過靜態著法
            if (futilityPruning && i > 0 && !isCapture && !isKiller)
            {
                continue;
            }

            board.MakeMove(move);

            // 8. 將軍延伸（Check Extension）
            bool givesCheck = board.IsCheck(board.Turn);
            bool recapturable = isCapture && IsCurrentSquareRecapturable(move.To);
            // M1a：吃子延伸放寬至 ply <= 3（原 ply <= 1）
            bool extendCapture = isCapture
                && ply <= 3
                && ShouldExtendForCapture(movingPiece, targetPiece, recapturable);
            bool extendThreat = !isCapture
                && !givesCheck
                && threatCandidate
                && !hadImmediateThreat
                && CreatesImmediateThreatFromCurrentPosition();
            // M1a：extensionBudget - 每條路徑最多延伸 6 次，防止指數爆炸
            int extension = (givesCheck || extendCapture || extendThreat) ? 1 : 0;
            // SE：若奇異延伸已觸發，且當前走法為 TT 最佳走法，再加一層延伸
            if (singularExtension && move == ttMove) extension += 1;
            if (ply >= 6) extension = 0;

            int score;

            // 9. 後序著法減枝（H2c：基於 history 的 LMR）
            int histScore = historyTable[move.From, move.To];
            int reduction = 0;
            if (!isCapture && !givesCheck && !isKiller && !inCheck)
            {
                reduction = ComputeLmrReduction(i, depth, histScore);
            }

            if (reduction > 0)
            {
                score = -Negamax(depth - 1 - reduction + extension, ply + 1, -beta, -alpha, false,
                    move.From, move.To);
                if (score > alpha)
                {
                    score = -Negamax(depth - 1 + extension, ply + 1, -beta, -alpha, false,
                        move.From, move.To);
                }
            }
            else
            {
                score = -Negamax(depth - 1 + extension, ply + 1, -beta, -alpha, false,
                    move.From, move.To);
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

                    // H1：alpha 更新時更新 Triangular PV Table
                    UpdatePv(ply, move);

                    if (alpha >= beta)
                    {
                        if (!isCapture)
                        {
                            UpdateKillers(ply, move);
                            historyTable[move.From, move.To] += depth * depth;
                            // H2b：記錄反制著法（對手上一步 → 此著法）
                            if (opponentLastFrom >= 0 && opponentLastTo >= 0)
                            {
                                countermoveTable[opponentLastFrom, opponentLastTo] = move;
                            }
                        }

                        tt.Store(board.ZobristKey, beta, depth, TTFlag.LowerBound, move);
                        return beta;
                    }
                }
            }
        }

        tt.Store(board.ZobristKey, bestScore, depth, flag, bestMove);
        return bestScore;
    }

    private int Quiescence(int alpha, int beta, int ply)
    {
        CheckPauseOrCancellation();
        Interlocked.Increment(ref nodesVisited);

        int eval = evaluator.Evaluate(board);
        if (eval >= beta) return beta;
        if (eval > alpha) alpha = eval;

        if (ply >= QuiescenceMaxPly) return alpha;

        var captures = board.GenerateLegalMoves()
            .Where(m => !board.GetPiece(m.To).IsNone)
            .ToList();

        if (captures.Count == 0) return alpha;

        OrderMoves(captures, Move.Null, 0);

        foreach (var move in captures)
        {
            CheckPauseOrCancellation();

            board.MakeMove(move);
            int score = -Quiescence(-beta, -alpha, ply + 1);
            board.UnmakeMove(move);

            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }

        return alpha;
    }

    // --- H1：Triangular PV Table ---

    /// <summary>
    /// 取得根部 PV（主要變例）著法列表。
    /// 搜尋完成後由外部呼叫，用於顯示或傳入下一輪優先排序。
    /// </summary>
    internal IReadOnlyList<Move> GetRootPv()
    {
        int len = pvLength[0];
        if (len <= 0) return System.Array.Empty<Move>();
        var result = new Move[len];
        for (int i = 0; i < len; i++)
            result[i] = pvTable[0, i];
        return result;
    }

    /// <summary>
    /// 在 alpha 更新時，更新 Triangular PV Table。
    /// 將當前著法 move 放在 ply 層，再複製 ply+1 的子 PV。
    /// </summary>
    private void UpdatePv(int ply, Move move)
    {
        if (ply >= MaxSearchPly) return;
        pvTable[ply, 0] = move;
        int childLen = ply + 1 < MaxSearchPly ? pvLength[ply + 1] : 0;
        for (int i = 0; i < childLen && (ply + 1 + i) < MaxSearchPly; i++)
            pvTable[ply, i + 1] = pvTable[ply + 1, i];
        pvLength[ply] = 1 + childLen;
    }

    // --- H2a：歷史表衰減 ---

    /// <summary>
    /// 迭代加深新深度開始時衰減歷史表（所有值 /2）。
    /// 讓舊有歷史資訊逐漸淡出，優先參考最近一輪的搜尋結果。
    /// </summary>
    internal void DecayHistory()
    {
        for (int i = 0; i < 90; i++)
            for (int j = 0; j < 90; j++)
                historyTable[i, j] /= 2;
    }

    // H2b/測試輔助：反制著法表存取
    internal Move GetCountermove(int opponentFrom, int opponentTo) => countermoveTable[opponentFrom, opponentTo];
    internal void SetCountermove(int opponentFrom, int opponentTo, Move move) => countermoveTable[opponentFrom, opponentTo] = move;

    // 測試輔助：歷史表存取
    internal int GetHistoryScore(int from, int to) => historyTable[from, to];
    internal void SetHistoryScore(int from, int to, int value) => historyTable[from, to] = value;

    /// <summary>
    /// 測試輔助：以排除指定走法的方式執行 Negamax（用於驗證 SE 排除搜尋機制）。
    /// </summary>
    internal int SearchWithExcludedMove(int depth, Move excludedMove)
    {
        CheckPauseOrCancellation();
        return Negamax(depth, 0, -Infinity, Infinity, skipNullMove: false,
            opponentLastFrom: -1, opponentLastTo: -1, excludedMove: excludedMove);
    }

    // 測試輔助：設定 killer 著法
    internal void SetKiller(int ply, Move move)
    {
        if (ply >= MaxSearchPly) return;
        killerMoves[ply, 1] = killerMoves[ply, 0];
        killerMoves[ply, 0] = move;
    }

    /// <summary>
    /// 測試輔助：帶對手上一步資訊的著法評分（公開版本）。
    /// </summary>
    internal int ScoreMovePublic(Move move, Move ttMove, int ply, int opponentLastFrom, int opponentLastTo)
        => ScoreMove(move, ttMove, ply, opponentLastFrom, opponentLastTo);

    // --- H2c：History-based LMR ---

    /// <summary>
    /// 根據著法索引、搜尋深度和歷史分數計算 LMR 減量。
    /// 歷史分數高的著法表示在之前的搜尋中表現良好，給予較少的減量。
    /// </summary>
    internal int ComputeLmrReduction(int moveIndex, int depth, int historyScore)
    {
        // 前 4 個著法或深度 < 3 不進行 LMR
        if (moveIndex < 4 || depth < 3) return 0;

        // 基礎減量
        int baseReduction = moveIndex >= 8 ? 2 : 1;

        // H2c：根據歷史分數動態調整
        // 歷史分數高（此著法表現良好）→ 減少減量
        // 閾值：4000 以上為高分（depth*depth 最大約 depth=20 → 400，累積可達 4000+）
        if (historyScore >= 4000) baseReduction = Math.Max(0, baseReduction - 1);

        return baseReduction;
    }

    // --- 著法排序 ---

    internal void OrderMoves(List<Move> moves, Move ttMove, int ply)
        => OrderMoves(moves, ttMove, ply, opponentLastFrom: -1, opponentLastTo: -1);

    internal void OrderMoves(List<Move> moves, Move ttMove, int ply, int opponentLastFrom, int opponentLastTo)
    {
        var scored = new int[moves.Count];
        for (int i = 0; i < moves.Count; i++)
        {
            scored[i] = ScoreMove(moves[i], ttMove, ply, opponentLastFrom, opponentLastTo);
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

    internal int ScoreMove(Move move, Move ttMove, int ply)
        => ScoreMove(move, ttMove, ply, opponentLastFrom: -1, opponentLastTo: -1);

    internal int ScoreMove(Move move, Move ttMove, int ply, int opponentLastFrom, int opponentLastTo)
    {
        if (move == ttMove) return 1_000_000;

        var movingPiece = board.GetPiece(move.From);
        var targetPiece = board.GetPiece(move.To);

        if (movingPiece.IsNone) return int.MinValue;

        if (!targetPiece.IsNone)
        {
            int victimVal = PieceValues[(int)targetPiece.Type];
            int attackerVal = PieceValues[(int)movingPiece.Type];
            int score = 100_000 + victimVal * 10 - attackerVal;
            if (ply == 0)
            {
                score += EvaluateCaptureSafety(move, movingPiece, targetPiece);
            }
            return score;
        }

        if (ply == 0 && MoveGivesCheck(move))
        {
            return CheckMoveBonus + historyTable[move.From, move.To];
        }

        if (ply < MaxSearchPly)
        {
            if (move == killerMoves[ply, 0]) return 90_000;
            if (move == killerMoves[ply, 1]) return 89_000;
        }

        // H2b：反制著法啟發式（優先級低於 killer，高於純 history）
        if (opponentLastFrom >= 0 && opponentLastTo >= 0
            && countermoveTable[opponentLastFrom, opponentLastTo] == move)
        {
            return 88_000 + historyTable[move.From, move.To];
        }

        return historyTable[move.From, move.To];
    }

    internal bool ShouldExtendForCapture(Move move)
    {
        var movingPiece = board.GetPiece(move.From);
        var targetPiece = board.GetPiece(move.To);
        if (movingPiece.IsNone || targetPiece.IsNone)
        {
            return false;
        }

        return ShouldExtendForCapture(movingPiece, targetPiece, IsMoveRecapturable(move));
    }

    internal bool CreatesImmediateThreat(Move move)
    {
        bool hadThreatBefore = SideToMoveHasHighValueCapture();
        board.MakeMove(move);
        try
        {
            return !hadThreatBefore && CreatesImmediateThreatFromCurrentPosition();
        }
        finally
        {
            board.UnmakeMove(move);
        }
    }

    private void UpdateKillers(int ply, Move move)
    {
        if (ply >= MaxSearchPly) return;

        if (move != killerMoves[ply, 0])
        {
            killerMoves[ply, 1] = killerMoves[ply, 0];
            killerMoves[ply, 0] = move;
        }
    }

    private bool IsKillerMove(int ply, Move move)
    {
        if (ply >= MaxSearchPly) return false;
        return move == killerMoves[ply, 0] || move == killerMoves[ply, 1];
    }

    // --- 輔助邏輯 ---

    private bool HasSufficientMaterial()
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

    private int EvaluateCaptureSafety(Move move, Piece movingPiece, Piece targetPiece)
    {
        if (movingPiece.IsNone || targetPiece.IsNone)
        {
            return 0;
        }

        if (IsMoveRecapturable(move))
        {
            return -PieceValues[(int)movingPiece.Type] * GuardedCapturePenaltyMultiplier;
        }

        return SafeCaptureBonus;
    }

    private bool MoveGivesCheck(Move move)
    {
        board.MakeMove(move);
        try
        {
            return board.IsCheck(board.Turn);
        }
        finally
        {
            board.UnmakeMove(move);
        }
    }

    private bool IsMoveRecapturable(Move move)
    {
        board.MakeMove(move);
        try
        {
            return IsCurrentSquareRecapturable(move.To);
        }
        finally
        {
            board.UnmakeMove(move);
        }
    }

    private bool IsCurrentSquareRecapturable(int square)
    {
        foreach (var reply in board.GenerateLegalMoves())
        {
            if (reply.To == square)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldExtendForCapture(Piece movingPiece, Piece targetPiece, bool recapturable)
    {
        if (movingPiece.IsNone || targetPiece.IsNone)
        {
            return false;
        }

        int victimValue = PieceValues[(int)targetPiece.Type];
        if (victimValue >= HighValueTacticalThreshold)
        {
            return true;
        }

        return !recapturable;
    }

    private bool CreatesImmediateThreatFromCurrentPosition()
    {
        board.MakeNullMove();
        try
        {
            foreach (var threatMove in board.GenerateLegalMoves())
            {
                var targetPiece = board.GetPiece(threatMove.To);
                if (!targetPiece.IsNone && PieceValues[(int)targetPiece.Type] >= HighValueTacticalThreshold)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            board.UnmakeNullMove();
        }
    }

    private bool SideToMoveHasHighValueCapture()
    {
        foreach (var move in board.GenerateLegalMoves())
        {
            var targetPiece = board.GetPiece(move.To);
            if (!targetPiece.IsNone && PieceValues[(int)targetPiece.Type] >= HighValueTacticalThreshold)
            {
                return true;
            }
        }

        return false;
    }

    private void CheckPauseOrCancellation()
    {
        // 使用 hardStopCt（僅使用者明確停止）等待，避免時間限制到期時破壞暫停狀態
        // 時間限制（ct）到期不應中斷暫停；只有明確 Stop 才能跳出
        pauseSignal.Wait(hardStopCt);
        if (ct.IsCancellationRequested || hardStopCt.IsCancellationRequested)
            throw new OperationCanceledException();
    }
}
