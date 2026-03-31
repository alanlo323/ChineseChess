using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Constants;
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
    private readonly EvalCache evalCache;
    private readonly CancellationToken ct;         // 時間限制 + 使用者停止（合併）
    private readonly CancellationToken hardStopCt; // 僅使用者明確停止（暫停等待用）
    private readonly ManualResetEventSlim pauseSignal;
    private readonly IProbCutDataCollector probCutCollector;

    private long nodesVisited;
    private Move[,] killerMoves;
    private int[,] historyTable;

    // 預計算 LMR 對數表：LmrTable[depth, moveIndex] = (int)(log(depth) * log(moveIndex) / 2.0)
    // 靜態唯讀，所有 worker 共用，無 GC 壓力
    private static readonly int[,] LmrTable = BuildLmrTable();

    private static int[,] BuildLmrTable()
    {
        var table = new int[128, 128];
        for (int d = 1; d < 128; d++)
            for (int m = 1; m < 128; m++)
                table[d, m] = (int)(Math.Log(d) * Math.Log(m) / 2.0);
        return table;
    }
    // H2b：反制著法表（countermoveTable[opponentFrom, opponentTo] = bestCounterMove）
    private Move[,] countermoveTable;
    // H3：Continuation History（contHistory[prevTo * ContHistStride + currFrom * 90 + currTo]）
    // 記錄「在對手走至某格之後，當前著法的效果」，比純 from-to 歷史更捕捉棋面脈絡
    // 大小：90 × 90 × 90 = 729,000 int ≈ 2.8MB per worker
    private int[] contHistory;
    // H1：Triangular PV Table（pvTable[ply, plyOffset] 存放 PV 著法，pvLength[ply] 存放長度）
    private readonly Move[,] pvTable;
    private readonly int[] pvLength;
    // 每次搜尋前可動態調整，避免 check extension 在淺層搜尋中無限延伸
    internal int effectiveMaxPly = MaxSearchPly;

    // WXF 搜尋路徑追蹤（per-ply，固定大小，無 GC 壓力）
    private readonly ulong[]              plyZobristKeys    = new ulong[MaxSearchPly + 1];
    private readonly MoveClassification[] plyClassifications = new MoveClassification[MaxSearchPly];
    private readonly PieceColor[]         plyTurns           = new PieceColor[MaxSearchPly];

    // WXF 長將/長捉判決分數（低於 CheckmateThreshold=15000，高於任何靜態估值）
    private const int WxfRepetitionWinScore = 10000;

    private const int Infinity = 30000;
    // 公開 Infinity 值，供 SearchEngine 使用（Aspiration Window 回退全窗口）
    internal const int InfinityValue = Infinity;
    // Lazy Evaluation：EvaluateFast 與 Evaluate 的允許差距邊際
    private const int LazyMargin = 200;
    private const int MateScore = GameConstants.MateScore;
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

    internal static readonly int[] PieceValues = { 0, 10000, 120, 120, 270, 600, 285, 30 };
    private static readonly int[] FutilityMargins = { 0, 200, 500 };
    // QSearch Delta Pruning 邊際：最大吃子收益（車=600）+ 50 安全邊際
    private const int QSearchDeltaMargin = 650;
    // 個別著法 Delta Pruning 附加邊際（允許略低估）
    private const int QSearchMoveDeltaMargin = 50;
    // Continuation History 索引步幅：prevTo(90) × currFrom(90) = 90×90
    private const int ContHistStride = 90 * 90;
    // ProbCut 相關常數
    private const int ProbCutMinDepth = 5;          // 觸發 ProbCut 的最小搜尋深度
    private const int ProbCutMargin = 120;           // probBeta = beta + ProbCutMargin
    private const int ProbCutReduction = 4;          // ProbCut 淺層搜尋的深度縮減量
    private const int ProbCutRepetitionLookback = 8; // 最近 8 ply 有重複即視為循環風險，禁用 ProbCut

    public long NodesVisited => Interlocked.Read(ref nodesVisited);

    /// <summary>是否啟用 ProbCut（預設 true；可在測試中關閉以比較節點數）。</summary>
    internal bool ProbCutEnabled { get; set; } = true;

    /// <summary>
    /// 是否啟用 ProbCut 回歸資料收集模式（預設 false）。
    /// 啟用時，每次 ProbCut 的淺搜/深搜結果都會記錄至 probCutCollector。
    /// 注意：開啟後 ProbCut 的剪枝效果暫時停用（僅收集資料），搜尋速度會下降。
    /// </summary>
    internal bool DataCollectionMode { get; set; } = false;

    /// <summary>
    /// ProbCut 成功觸發次數（成功 beta-cutoff）。
    /// 用於調參：可比較 on/off 情境下的觸發率與節點縮減效益。
    /// </summary>
    internal int ProbCutCutCount { get; private set; }

    /// <summary>
    /// ProbCut QSearch 預篩通過但深層 Negamax 未確認的次數（false-positive 計數）。
    /// 誤判率 = FalseCount / (CutCount + FalseCount)。
    /// 每次 Search() 開頭重置為 0。
    /// </summary>
    internal int ProbCutFalseCount { get; private set; }

    public SearchWorker(IBoard board, IEvaluator evaluator, TranspositionTable tt, EvalCache evalCache, CancellationToken ct, CancellationToken hardStopCt, ManualResetEventSlim pauseSignal, IProbCutDataCollector? collector = null)
    {
        this.board = board;
        this.evaluator = evaluator;
        this.tt = tt;
        this.evalCache = evalCache;
        this.ct = ct;
        this.hardStopCt = hardStopCt;
        this.pauseSignal = pauseSignal;
        probCutCollector = collector ?? NullProbCutDataCollector.Instance;
        killerMoves = new Move[MaxSearchPly, 2];
        historyTable = new int[90, 90];
        countermoveTable = new Move[90, 90];
        contHistory = new int[90 * 90 * 90];
        pvTable = new Move[MaxSearchPly, MaxSearchPly];
        pvLength = new int[MaxSearchPly];

        // NNUE：搜尋開始前刷新累加器（HandcraftedEvaluator 的 no-op）
        evaluator.RefreshAccumulator(board);
    }

    /// <summary>
    /// 從深度 1 持續進行迭代加深到 targetDepth。
    /// 回傳在已完成的最深層深度中找到的最佳結果。
    /// </summary>
    public SearchResult Search(int targetDepth)
    {
        nodesVisited = 0;
        ProbCutCutCount = 0;
        ProbCutFalseCount = 0;
        Array.Clear(killerMoves, 0, killerMoves.Length);
        Array.Clear(historyTable, 0, historyTable.Length);
        Array.Clear(pvLength, 0, pvLength.Length);
        CheckPauseOrCancellation();

        var result = new SearchResult();

        try
        {
            int prevScore = 0;
            int prevPrevScore = 0; // 自適應 Aspiration Window delta 計算
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
                    // 自適應 delta：以前兩層分數差估算期望波動
                    int delta = Math.Clamp(Math.Abs(prevScore - prevPrevScore) + 25, 25, 100);
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

                prevPrevScore = prevScore;
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
        plyZobristKeys[0] = board.ZobristKey; // 根節點種子 key
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
            var movedPiece    = board.GetPiece(move.From);
            var capturedPiece = board.GetPiece(move.To);
            try
            {
                board.MakeMove(move);
                evaluator.OnMakeMove(board, move, movedPiece, capturedPiece);
                // Negamax 在 MakeMove 後已切換行棋方，取負號還原為「做出此走法的玩家」視角
                int score = -Negamax(searchDepth, 1, -Infinity, Infinity, false);
                evaluator.OnUndoMove(board, move);
                board.UnmakeMove(move);

                results.Add((move, score));
                if (score > bestScore) bestScore = score;

                string bestStr = bestScore > 0 ? $"+{bestScore}" : bestScore.ToString();
                progress?.Report($"智能提示分析中（深度 {depth}，{threadLabel}）：{i + 1}/{total} 走法，目前最佳 {bestStr}");
            }
            catch (OperationCanceledException)
            {
                // MakeMove 已執行，Negamax 中取消 → 還原棋盤後停止
                evaluator.OnUndoMove(board, move);
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
        long visited = Interlocked.Increment(ref nodesVisited);
        if (visited % 2000 == 0)
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

        // 2. 和局早返（重覆局面 OR 無吃子超限 OR 棋子不足）
        if (ply > 0)
        {
            if (board.IsDrawByNoCapture()) return 0;
            if (board.IsDrawByInsufficientMaterial()) return 0;
            if (board.IsDrawByRepetition(threshold: 3))
                return EvaluateSearchRepetitionVerdict(ply);
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

        // 5. ProbCut（機率剪枝）：深度達門檻，以淺層搜尋快速排除明顯不利節點
        //
        // 觸發條件（全部須滿足）：
        //   depth >= ProbCutMinDepth          — 淺層搜尋不值得此開銷
        //   ply > 0                           — 根節點必須精確，不得提早返回
        //   !inCheck                          — 將軍局面必須完整搜尋所有應將著法
        //   !isPvNode                         — PV 路徑需精確展開
        //   !skipNullMove                     — 代理「前向剪枝上下文」：null-move/SE 排除搜尋內不連鎖觸發
        //   |beta| < CheckmateThreshold       — 不在將殺範圍，避免誤剪將殺路線
        //   excludedMove.IsNull               — SE 排除搜尋節點必須精確
        //   !IsAnyRepetitionInLastN(8)        — 重複風險守衛（見下方說明）
        //
        // 重複風險守衛（中國象棋特化）：
        //   若最近 8 ply 內有任何局面重複出現，代表局面正在循環。
        //   WXF 規則下，重複可能是勝/負（長將/長捉判負）而非和棋；
        //   重複局面已透過 EvaluateSearchRepetitionVerdict 處理（threshold:3，WXF 裁決）。
        //   此守衛確保在潛在循環路徑上禁用 ProbCut，回到完整搜尋以避免誤剪。
        //
        // 候選著法過濾（炮吃子排除）：
        //   象棋炮的吃子需要炮台（跳吃規則），吃子後炮台結構改變，
        //   後續戰術可能劇烈變化（雙炮、串打），SEE 無法精確評估此類波動。
        //   因此排除炮作為進攻方的吃子，避免機率剪枝誤判高波動局面。
        //
        // 兩階段驗證：
        //   步驟一：QSearch 快速預篩（廉價） → 通過才升級到 Negamax
        //   步驟二：Negamax(depth - ProbCutReduction - 1) 精確確認
        //   成功後存入 TT（下界），供後續相同局面受益
        // DataCollectionMode：資料收集時略過 !isPvNode 限制，
        // 允許在 PV 節點也記錄觀測（提供更完整的資料集供回歸分析）。
        if (ProbCutEnabled
            && depth >= ProbCutMinDepth
            && ply > 0
            && !inCheck
            && (!isPvNode || DataCollectionMode)
            && !skipNullMove
            && Math.Abs(beta) < CheckmateThreshold
            && excludedMove.IsNull
            && !board.IsAnyRepetitionInLastN(ProbCutRepetitionLookback))
        {
            int probBeta = beta + ProbCutMargin;
            // 精確靜態評估：EvaluateFast 誤差可達 LazyMargin=200，
            // 用於 SEE 門檻計算會讓篩選偏差；Evaluate 確保 staticEval + SEE >= probBeta 的判斷準確
            int staticEvalForProb = evaluator.Evaluate(board);

            // 候選著法：僅高信心吃子，排除炮作為進攻方（高波動，SEE 難以精確估算）
            var probCaptures = board.GenerateCaptureMoves()
                .Where(m => board.GetPiece(m.From).Type != PieceType.Cannon)
                .ToList();

            if (probCaptures.Count > 0)
            {
                OrderMoves(probCaptures, ttMove, ply, opponentLastFrom, opponentLastTo);

                foreach (var captureMove in probCaptures)
                {
                    // SEE 篩選：靜態評估 + SEE 必須有機會達到 probBeta
                    // DataCollectionMode 略過此過濾，以收集所有候選著法的完整資料（包含不利吃子）
                    if (!DataCollectionMode &&
                        StaticExchangeEvaluator.See(board, captureMove, PieceValues) < probBeta - staticEvalForProb)
                        continue;

                    var probMovedPiece    = board.GetPiece(captureMove.From);
                    var probCapturedPiece = board.GetPiece(captureMove.To);
                    board.MakeMove(captureMove);
                    evaluator.OnMakeMove(board, captureMove, probMovedPiece, probCapturedPiece);

                    // givesCheck 守衛（WXF 特化）：
                    // MakeMove 後 board.Turn 已切換為對手方，IsCheck(board.Turn) 正確檢查
                    // 「此吃子著法是否將軍對手」。WXF 長將判負屬於特殊語義，
                    // ProbCut 機率邊界無法涵蓋，須排除此類候選避免誤剪。
                    if (!DataCollectionMode && board.IsCheck(board.Turn))
                    {
                        evaluator.OnUndoMove(board, captureMove);
                        board.UnmakeMove(captureMove);
                        continue;
                    }

                    // 步驟一：QSearch 預篩（廉價）——即使不做靜態著法也已達 probBeta 才繼續
                    int probScore = -Quiescence(-probBeta, -probBeta + 1, 0);

                    // 步驟二：DataCollectionMode 時強制執行深搜；否則只在 QSearch 通過後才搜尋
                    int probDeepScore = int.MinValue;
                    if (probScore >= probBeta || DataCollectionMode)
                    {
                        probDeepScore = -Negamax(depth - ProbCutReduction - 1, ply + 1,
                            -probBeta, -probBeta + 1, skipNullMove: false,
                            captureMove.From, captureMove.To);
                    }

                    evaluator.OnUndoMove(board, captureMove);
                    board.UnmakeMove(captureMove);

                    // FalseCount：QSearch 預篩通過但深搜未確認（false positive）
                    if (!DataCollectionMode && probScore >= probBeta
                        && probDeepScore != int.MinValue && probDeepScore < probBeta)
                    {
                        ProbCutFalseCount++;
                    }

                    // 資料收集：記錄此次觀測（不影響搜尋結果）
                    if (DataCollectionMode && probDeepScore != int.MinValue)
                    {
                        probCutCollector.RecordSample(new ProbCutSample(
                            ShallowScore: probScore,
                            DeepScore: probDeepScore,
                            BetaUsed: probBeta,
                            Depth: depth,
                            Ply: ply,
                            DepthPair: ClassifyDepthPair(depth),
                            Phase: ClassifyPhase(board),
                            CaptureClass: ClassifyCaptureClass(board.GetPiece(captureMove.From).Type)));
                    }

                    // 正常 ProbCut 剪枝（DataCollectionMode 時跳過，以觀察完整資料）
                    if (!DataCollectionMode && probDeepScore >= probBeta)
                    {
                        // 存入 TT（下界）：後續相同局面可直接使用此結果
                        tt.Store(board.ZobristKey, probBeta, depth - ProbCutReduction,
                            TTFlag.LowerBound, captureMove);
                        ProbCutCutCount++;
                        return probBeta;
                    }
                }
            }
        }

        // 6. Futility 剪枝旗標
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

        // 7. 分段產生著法（Stage 1：吃子，Stage 2：安靜）
        // 優化：Stage 1 產生 beta 剪枝時，Stage 2 的安靜著法完全跳過不生成。
        var captureList = board.GenerateCaptureMoves().ToList();
        OrderMoves(captureList, ttMove, ply, opponentLastFrom, opponentLastTo);

        int moveCount = 0;
        int bestScore = -Infinity;
        Move bestMove = Move.Null;
        TTFlag flag = TTFlag.UpperBound;

        // Stage 1：吃子著法
        foreach (var move in captureList)
        {
            // 排除搜尋：跳過指定的排除走法
            if (!excludedMove.IsNull && move == excludedMove) continue;

            var movingPiece = board.GetPiece(move.From);
            var targetPiece = board.GetPiece(move.To);
            bool isKiller = IsKillerMove(ply, move);

            board.MakeMove(move);
            evaluator.OnMakeMove(board, move, movingPiece, targetPiece);

            bool givesCheck = board.IsCheck(board.Turn);
            plyZobristKeys[ply + 1]  = board.ZobristKey;
            plyTurns[ply]            = movingPiece.Color;
            plyClassifications[ply]  = ClassifyForSearch(move, movingPiece, targetPiece, givesCheck);
            bool recapturable = IsCurrentSquareRecapturable(move.To);
            bool extendCapture = ply <= 3 && ShouldExtendForCapture(movingPiece, targetPiece, recapturable);
            int extension = (givesCheck || extendCapture) ? 1 : 0;
            if (singularExtension && move == ttMove) extension += 1;
            if (ply >= 6) extension = 0;

            int score;
            // 吃子著法不套用 LMR（isCapture == true）
            score = -Negamax(depth - 1 + extension, ply + 1, -beta, -alpha, false,
                move.From, move.To);

            evaluator.OnUndoMove(board, move);
            board.UnmakeMove(move);

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;

                if (score > alpha)
                {
                    alpha = score;
                    flag = TTFlag.Exact;
                    UpdatePv(ply, move);

                    if (alpha >= beta)
                    {
                        tt.Store(board.ZobristKey, beta, depth, TTFlag.LowerBound, move);
                        return beta;
                    }
                }
            }

            moveCount++;
        }

        // Stage 2：安靜著法（只在 Stage 1 未提前返回時才生成）
        var quietList = board.GenerateQuietMoves().ToList();
        OrderMoves(quietList, ttMove, ply, opponentLastFrom, opponentLastTo);

        int quietIdx = 0;
        foreach (var move in quietList)
        {
            // 排除搜尋：跳過指定的排除走法
            if (!excludedMove.IsNull && move == excludedMove) { quietIdx++; continue; }

            // Stage 2 的 moveIndex：延續 Stage 1 的計數，維持 LMR 一致性
            int i = captureList.Count + quietIdx;

            var movingPiece = board.GetPiece(move.From);
            var targetPiece = board.GetPiece(move.To);
            bool isKiller = IsKillerMove(ply, move);
            // M1b：威脅延伸（安靜著法）
            bool threatCandidate = ply <= 2 && depth >= 2 && depth <= 3 && i < 8;
            bool hadImmediateThreat = threatCandidate && SideToMoveHasHighValueCapture();

            // Futility 剪枝：若無望則略過安靜著法（第一個著法不剪）
            if (futilityPruning && moveCount > 0 && !isKiller)
            {
                quietIdx++;
                continue;
            }

            board.MakeMove(move);
            evaluator.OnMakeMove(board, move, movingPiece, targetPiece);

            bool givesCheck = board.IsCheck(board.Turn);
            plyZobristKeys[ply + 1]  = board.ZobristKey;
            plyTurns[ply]            = movingPiece.Color;
            plyClassifications[ply]  = ClassifyForSearch(move, movingPiece, targetPiece, givesCheck);
            bool extendThreat = !givesCheck
                && threatCandidate
                && !hadImmediateThreat
                && CreatesImmediateThreatFromCurrentPosition();
            int extension = (givesCheck || extendThreat) ? 1 : 0;
            if (singularExtension && move == ttMove) extension += 1;
            if (ply >= 6) extension = 0;

            int score;
            // 9. 後序著法減枝（H2c：基於 history 的 LMR）
            int histScore = historyTable[move.From, move.To];
            int reduction = 0;
            if (!givesCheck && !isKiller && !inCheck)
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

            evaluator.OnUndoMove(board, move);
            board.UnmakeMove(move);

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;

                if (score > alpha)
                {
                    alpha = score;
                    flag = TTFlag.Exact;
                    UpdatePv(ply, move);

                    if (alpha >= beta)
                    {
                        UpdateKillers(ply, move);
                        historyTable[move.From, move.To] = Math.Min(historyTable[move.From, move.To] + depth * depth, 16384);
                        // H2b：記錄反制著法（對手上一步 → 此著法）
                        if (opponentLastFrom >= 0 && opponentLastTo >= 0)
                        {
                            countermoveTable[opponentLastFrom, opponentLastTo] = move;
                            // H3：更新 Continuation History
                            int contIdx = opponentLastTo * ContHistStride + move.From * 90 + move.To;
                            contHistory[contIdx] = Math.Min(contHistory[contIdx] + depth * depth, 16384);
                        }

                        tt.Store(board.ZobristKey, beta, depth, TTFlag.LowerBound, move);
                        return beta;
                    }
                }
            }

            moveCount++;
            quietIdx++;
        }

        // 空節點：將殺或逼和
        // moveCount == 0 涵蓋兩種情況：
        //   1. 無著法（stalemate / checkmate）
        //   2. Singular Extension：全部著法均為 excludedMove（回傳 -Infinity 讓 SE 正確觸發）
        if (moveCount == 0)
        {
            if (inCheck) return -MateScore + ply;
            return 0;
        }

        tt.Store(board.ZobristKey, bestScore, depth, flag, bestMove);
        return bestScore;
    }

    private int Quiescence(int alpha, int beta, int ply)
    {
        CheckPauseOrCancellation();
        Interlocked.Increment(ref nodesVisited);

        // 評估快取查詢：相同局面（含行棋方）直接取用，跳過完整評估計算
        ulong evalKey = board.ZobristKey;
        if (!evalCache.TryGet(evalKey, out int eval))
        {
            eval = evaluator.Evaluate(board);
            evalCache.Store(evalKey, eval);
        }
        if (eval >= beta) return beta;

        // Delta Pruning（整體）：若靜態評估加上最大可能收益仍低於 alpha，
        // 整個局面已無望達到 alpha，直接剪枝。
        // 注意：必須在 alpha = eval 之前執行，否則條件永遠為 false（eval + 650 < eval）
        if (eval + QSearchDeltaMargin < alpha) return alpha;

        if (eval > alpha) alpha = eval;

        if (ply >= QuiescenceMaxPly) return alpha;

        var captures = board.GenerateCaptureMoves().ToList();

        if (captures.Count == 0) return alpha;

        OrderMoves(captures, Move.Null, 0);

        foreach (var move in captures)
        {
            CheckPauseOrCancellation();

            // SEE 過濾：跳過明顯不利的吃子（SEE < 0），減少靜止搜尋的爆炸性擴展
            if (StaticExchangeEvaluator.See(board, move, PieceValues) < 0) continue;

            // Delta Pruning（個別著法）：若此著法的最大收益仍無法達到 alpha，跳過。
            // 炮作為進攻方時豁免：跳吃機制讓靜態收益難以估算（與 ProbCut 的炮排除邏輯一致）
            // GenerateCaptureMoves 保證 move.To 有棋子，不需額外 IsNone 檢查
            var capturedPiece = board.GetPiece(move.To);
            var attackingPiece = board.GetPiece(move.From);
            if (attackingPiece.Type != PieceType.Cannon &&
                eval + PieceValues[(int)capturedPiece.Type] + QSearchMoveDeltaMargin < alpha)
                continue;

            board.MakeMove(move);
            evaluator.OnMakeMove(board, move, attackingPiece, capturedPiece);
            int score = -Quiescence(-beta, -alpha, ply + 1);
            evaluator.OnUndoMove(board, move);
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

        // H3：Continuation History 同步衰減，讓舊資訊逐漸淡出
        for (int i = 0; i < contHistory.Length; i++)
            contHistory[i] /= 2;
    }

    // H2b/測試輔助：反制著法表存取
    internal Move GetCountermove(int opponentFrom, int opponentTo) => countermoveTable[opponentFrom, opponentTo];
    internal void SetCountermove(int opponentFrom, int opponentTo, Move move) => countermoveTable[opponentFrom, opponentTo] = move;

    // 測試輔助：歷史表存取
    internal int GetHistoryScore(int from, int to) => historyTable[from, to];
    internal void SetHistoryScore(int from, int to, int value) => historyTable[from, to] = value;

    // H3/測試輔助：Continuation History 存取
    internal int GetContHistoryScore(int prevTo, int currFrom, int currTo)
        => contHistory[prevTo * ContHistStride + currFrom * 90 + currTo];
    internal void SetContHistoryScore(int prevTo, int currFrom, int currTo, int value)
        => contHistory[prevTo * ContHistStride + currFrom * 90 + currTo] = value;

    /// <summary>
    /// 測試輔助：以排除指定走法的方式執行 Negamax（用於驗證 SE 排除搜尋機制）。
    /// </summary>
    internal int SearchWithExcludedMove(int depth, Move excludedMove)
    {
        CheckPauseOrCancellation();
        return Negamax(depth, 0, -Infinity, Infinity, skipNullMove: false,
            opponentLastFrom: -1, opponentLastTo: -1, excludedMove: excludedMove);
    }

    /// <summary>測試輔助：公開 ClassifyForSearch 方法。</summary>
    internal static MoveClassification ClassifyForSearchPublic(
        Move move, Piece movedPiece, Piece capturedPiece, bool givesCheck)
        => ClassifyForSearch(move, movedPiece, capturedPiece, givesCheck);

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

        // 對數公式（查預計算表）：比 step function 更平滑，深層搜尋效果更好
        // log(depth) × log(moveIndex) / 2.0；depth=6,m=8 ≈ 1.3；depth=12,m=16 ≈ 2.5
        int baseReduction = Math.Max(1, LmrTable[Math.Min(depth, 127), Math.Min(moveIndex, 127)]);

        // H2c：根據歷史分數動態調整
        // 歷史分數高（此著法表現良好）→ 減少減量
        if (historyScore >= 4000) baseReduction = Math.Max(0, baseReduction - 1);

        // 上限：不超過 depth/2，避免過度縮減
        return Math.Min(baseReduction, depth / 2);
    }

    // --- ProbCut 資料收集 Helper ---

    private static ProbCutDepthPair ClassifyDepthPair(int depth) => depth switch
    {
        5  => ProbCutDepthPair.D5_0,
        6  => ProbCutDepthPair.D6_1,
        7  => ProbCutDepthPair.D7_2,
        8  => ProbCutDepthPair.D8_3,
        9  => ProbCutDepthPair.D9_4,
        _  => ProbCutDepthPair.D10Plus
    };

    private static ProbCutPhase ClassifyPhase(IBoard currentBoard)
    {
        int phase = Evaluators.GamePhase.Calculate(currentBoard);
        if (phase >= 200) return ProbCutPhase.Opening;
        if (phase >= 80)  return ProbCutPhase.Midgame;
        return ProbCutPhase.Endgame;
    }

    private static ProbCutCaptureClass ClassifyCaptureClass(Domain.Enums.PieceType attackerType) =>
        attackerType switch
        {
            Domain.Enums.PieceType.Rook    => ProbCutCaptureClass.RookCapture,
            Domain.Enums.PieceType.Cannon  => ProbCutCaptureClass.CannonCapture,
            Domain.Enums.PieceType.Horse   => ProbCutCaptureClass.HorseCapture,
            Domain.Enums.PieceType.Advisor or
            Domain.Enums.PieceType.Elephant => ProbCutCaptureClass.MinorCapture,
            _                              => ProbCutCaptureClass.PawnCapture
        };

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
            int mvvLva = victimVal * 10 - attackerVal;
            // SEE 分類：有利吃子高優先，不利吃子低優先（排在平靜著法之後）
            int see = StaticExchangeEvaluator.See(board, move, PieceValues);
            return see >= 0 ? 100_000 + mvvLva : -50_000 + mvvLva;
        }

        // H3：Continuation History 分數（安靜著法）
        int contScore = opponentLastTo >= 0
            ? contHistory[opponentLastTo * ContHistStride + move.From * 90 + move.To]
            : 0;

        if (ply == 0 && MoveGivesCheck(move))
        {
            return CheckMoveBonus + historyTable[move.From, move.To] + contScore;
        }

        if (ply < MaxSearchPly)
        {
            if (move == killerMoves[ply, 0]) return 90_000;
            if (move == killerMoves[ply, 1]) return 89_000;
        }

        // H2b：反制著法啟發式（優先級低於 killer，高於純 history）
        // 注意：不加 contScore，確保此分數段（88_000 + history）不超過 killer 固定優先級
        if (opponentLastFrom >= 0 && opponentLastTo >= 0
            && countermoveTable[opponentLastFrom, opponentLastTo] == move)
        {
            return 88_000 + historyTable[move.From, move.To];
        }

        // H3：Continuation History 僅影響純 history 排序（最低優先級段）
        return historyTable[move.From, move.To] + contScore;
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
        var movedPiece    = board.GetPiece(move.From);
        var capturedPiece = board.GetPiece(move.To);
        board.MakeMove(move);
        evaluator.OnMakeMove(board, move, movedPiece, capturedPiece);
        try
        {
            return !hadThreatBefore && CreatesImmediateThreatFromCurrentPosition();
        }
        finally
        {
            evaluator.OnUndoMove(board, move);
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
        var movedPiece    = board.GetPiece(move.From);
        var capturedPiece = board.GetPiece(move.To);
        board.MakeMove(move);
        evaluator.OnMakeMove(board, move, movedPiece, capturedPiece);
        try
        {
            return board.IsCheck(board.Turn);
        }
        finally
        {
            evaluator.OnUndoMove(board, move);
            board.UnmakeMove(move);
        }
    }

    private bool IsMoveRecapturable(Move move)
    {
        var movedPiece    = board.GetPiece(move.From);
        var capturedPiece = board.GetPiece(move.To);
        board.MakeMove(move);
        evaluator.OnMakeMove(board, move, movedPiece, capturedPiece);
        try
        {
            return IsCurrentSquareRecapturable(move.To);
        }
        finally
        {
            evaluator.OnUndoMove(board, move);
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

    // --- WXF 重複局面裁決 ---

    /// <summary>
    /// 搜尋用輕量著法分類（不含 Chase 偵測，避免 GenerateLegalMoves 開銷）。
    /// givesCheck 由呼叫端已計算的 board.IsCheck(board.Turn) 提供。
    /// </summary>
    private static MoveClassification ClassifyForSearch(
        Move move, Piece movedPiece, Piece capturedPiece, bool givesCheck)
    {
        if (!capturedPiece.IsNone) return MoveClassification.Cancel;
        if (movedPiece.Type == PieceType.Pawn &&
            MoveClassifier.IsPawnAdvance(move, movedPiece.Color))
            return MoveClassification.Cancel;
        return givesCheck ? MoveClassification.Check : MoveClassification.Idle;
    }

    /// <summary>
    /// 根據搜尋路徑的 WXF 分析，回傳重複局面的正確分數。
    /// 相對當前行棋方：+WxfRepetitionWinScore=當前方贏，-= 當前方輸，0=和或未定。
    /// </summary>
    private int EvaluateSearchRepetitionVerdict(int ply)
    {
        // 建構 WxfRepetitionJudge 所需的歷史（種子 + ply 步）
        var history = new List<MoveRecord>(ply + 1);
        history.Add(new MoveRecord
        {
            ZobristKey     = plyZobristKeys[0],
            Turn           = board.Turn, // 種子條目，Turn 值不影響裁決
            Move           = Move.Null,
            Classification = MoveClassification.Cancel,
            VictimSquare   = -1,
            IsCapture      = false,
        });
        for (int i = 0; i < ply; i++)
        {
            history.Add(new MoveRecord
            {
                ZobristKey     = plyZobristKeys[i + 1],
                Turn           = plyTurns[i],
                Move           = Move.Null,
                Classification = plyClassifications[i],
                VictimSquare   = -1,
                IsCapture      = false,
            });
        }

        var verdict = WxfRepetitionJudge.Judge(history);
        return verdict switch
        {
            RepetitionVerdict.RedWins   =>
                board.Turn == PieceColor.Red   ?  WxfRepetitionWinScore : -WxfRepetitionWinScore,
            RepetitionVerdict.BlackWins =>
                board.Turn == PieceColor.Black ?  WxfRepetitionWinScore : -WxfRepetitionWinScore,
            RepetitionVerdict.Draw      => 0,
            _                           => 0, // None（重複鏈被打斷或不足 3 次）→ 保守和棋
        };
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
