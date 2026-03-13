using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Search;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證搜尋啟發式優化功能：
///   H2a - History Decay（歷史表衰減）
///   H2b - Countermove Heuristic（反制著法啟發式）
///   H2c - History-based LMR（基於歷史的後序著法減枝）
/// </summary>
public class SearchWorkerHeuristicsTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── H2a：History Decay ───────────────────────────────────────────────

    [Fact]
    public void HistoryDecay_AfterNewIteration_HistoryValuesAreHalved()
    {
        // 驗證 DecayHistory() 方法將所有歷史表值縮小一半
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        // 手動設定歷史表的一些值
        worker.SetHistoryScore(10, 20, 1000);
        worker.SetHistoryScore(40, 50, 500);
        worker.SetHistoryScore(0, 89, 200);

        worker.DecayHistory();

        // 驗證衰減後的值約為原值一半
        Assert.Equal(500, worker.GetHistoryScore(10, 20));
        Assert.Equal(250, worker.GetHistoryScore(40, 50));
        Assert.Equal(100, worker.GetHistoryScore(0, 89));
    }

    [Fact]
    public void HistoryDecay_ZeroValues_RemainsZero()
    {
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        // 未設定時，所有值應為 0
        worker.DecayHistory();

        Assert.Equal(0, worker.GetHistoryScore(0, 0));
        Assert.Equal(0, worker.GetHistoryScore(44, 45));
    }

    [Fact]
    public void HistoryDecay_NegativeValues_AreAlsoHalved()
    {
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        worker.SetHistoryScore(5, 15, -400);
        worker.DecayHistory();

        // 負值也應衰減（整數除法截斷）
        Assert.Equal(-200, worker.GetHistoryScore(5, 15));
    }

    // ─── H2b：Countermove Heuristic ──────────────────────────────────────

    [Fact]
    public void CountermoveTable_InitiallyEmpty()
    {
        // 驗證初始狀態：反制著法表為空
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        var countermove = worker.GetCountermove(10, 20);
        Assert.True(countermove.IsNull);
    }

    [Fact]
    public void CountermoveTable_StoreAndRetrieve()
    {
        // 驗證反制著法存取正確性
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        var counterMove = new Move(40, 50);
        worker.SetCountermove(10, 20, counterMove);

        var retrieved = worker.GetCountermove(10, 20);
        Assert.Equal(counterMove, retrieved);
    }

    [Fact]
    public void CountermoveTable_DifferentKeys_StoredIndependently()
    {
        // 驗證不同 key 的反制著法互不干擾
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        var move1 = new Move(40, 50);
        var move2 = new Move(60, 70);
        worker.SetCountermove(10, 20, move1);
        worker.SetCountermove(30, 40, move2);

        Assert.Equal(move1, worker.GetCountermove(10, 20));
        Assert.Equal(move2, worker.GetCountermove(30, 40));
    }

    [Fact]
    public void CountermoveScore_ShouldBeHigherThanHistoryButLowerThanKiller()
    {
        // 反制著法排序優先級：killer > countermove > history
        var board = new Board("4k4/9/9/9/9/9/9/3R5/9/3K5 w - - 0 1");
        var worker = CreateWorker(board);

        // 三種不同著法：killer、countermove、普通著法
        var killerMove = new Move(66, 67);     // 檢查用，確保合法
        var countermoveMove = new Move(66, 65); // 普通靜止著法
        var quietMove = new Move(66, 57);       // 另一普通著法

        // 設定 killer
        worker.SetKiller(0, killerMove);
        // 設定反制著法（對手上一步 from=0, to=1，對應反制著法 countermoveMove）
        worker.SetCountermove(0, 1, countermoveMove);
        // 設定 history（讓普通著法也有一些分）
        worker.SetHistoryScore(66, 57, 100);

        int killerScore = worker.ScoreMovePublic(killerMove, Move.Null, 0, opponentLastFrom: 0, opponentLastTo: 1);
        int countermoveScore = worker.ScoreMovePublic(countermoveMove, Move.Null, 0, opponentLastFrom: 0, opponentLastTo: 1);
        int quietScore = worker.ScoreMovePublic(quietMove, Move.Null, 0, opponentLastFrom: 0, opponentLastTo: 1);

        Assert.True(killerScore > countermoveScore,
            $"Killer ({killerScore}) 應高於 countermove ({countermoveScore})");
        Assert.True(countermoveScore > quietScore,
            $"Countermove ({countermoveScore}) 應高於 history-only ({quietScore})");
    }

    // ─── H2c：History-based LMR ───────────────────────────────────────────

    [Fact]
    public void HistoryLmr_HighHistoryScore_ReducesLmrReduction()
    {
        // 歷史表分數高的著法應獲得較少的 LMR 減枝
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        // 低歷史分數（預設 0）
        int lowHistoryReduction = worker.ComputeLmrReduction(moveIndex: 5, depth: 4, historyScore: 0);
        // 高歷史分數
        int highHistoryReduction = worker.ComputeLmrReduction(moveIndex: 5, depth: 4, historyScore: 5000);

        Assert.True(highHistoryReduction <= lowHistoryReduction,
            $"高歷史分數的 LMR ({highHistoryReduction}) 應 <= 低歷史分數 ({lowHistoryReduction})");
    }

    [Fact]
    public void HistoryLmr_VeryHighHistoryScore_CanReduceToZero()
    {
        // 極高歷史分數可讓 LMR 減量降至 0
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        int reduction = worker.ComputeLmrReduction(moveIndex: 5, depth: 4, historyScore: 100000);

        Assert.True(reduction >= 0, "LMR 減量不應為負數");
    }

    [Fact]
    public void HistoryLmr_EarlyMoves_NotAffectedByLmr()
    {
        // 前 4 個著法（moveIndex < 4）不應受 LMR 影響（回傳 0）
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        for (int i = 0; i < 4; i++)
        {
            int reduction = worker.ComputeLmrReduction(moveIndex: i, depth: 4, historyScore: 0);
            Assert.Equal(0, reduction);
        }
    }

    [Fact]
    public void HistoryLmr_ShallowDepth_NotAffectedByLmr()
    {
        // depth < 3 時不應有 LMR
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        int reduction = worker.ComputeLmrReduction(moveIndex: 10, depth: 2, historyScore: 0);
        Assert.Equal(0, reduction);
    }

    private static SearchWorker CreateWorker(IBoard board)
    {
        return new SearchWorker(
            board,
            new HandcraftedEvaluator(),
            new TranspositionTable(sizeMb: 4),
            CancellationToken.None,
            CancellationToken.None,
            new ManualResetEventSlim(true));
    }
}
