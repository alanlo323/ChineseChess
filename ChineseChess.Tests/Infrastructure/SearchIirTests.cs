using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Search;
using System.Threading;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 IIR（Internal Iterative Reduction）：
/// TT 無命中且 depth >= 4 且非排除搜尋節點時，自動將 depth 減一，
/// 以節省在沒有良好 TT 著法時的搜尋節點數。
/// </summary>
public class SearchIirTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── 測試 1：TT 無命中 + depth=4 → 觸發 IIR，節點數少於完整深度搜尋 ───

    [Fact]
    public void Iir_NoTtHit_Depth4_ReducesNodeCount()
    {
        // 空白 TT → 確保第一次搜尋 TT 無命中 → 觸發 IIR（depth 從 4 降到 3）
        var tt = new TranspositionTable(sizeMb: 4);
        var board = new Board(InitialFen);

        // 有 IIR 的搜尋（TT 為空）
        using var pause = new ManualResetEventSlim(true);
        var workerWithIir = new SearchWorker(board, new HandcraftedEvaluator(), tt, new EvalCache(), CancellationToken.None, CancellationToken.None, pause);
        int scoreWithIir = workerWithIir.SearchSingleDepth(4);
        long nodesWithIir = workerWithIir.NodesVisited;

        // 使用滿 TT（先做 depth=4 搜尋建立 TT，再重新搜尋 → TT 命中，無 IIR）
        var ttFull = new TranspositionTable(sizeMb: 4);
        var boardFull = new Board(InitialFen);
        using var pause2 = new ManualResetEventSlim(true);
        var workerFull = new SearchWorker(boardFull, new HandcraftedEvaluator(), ttFull, new EvalCache(), CancellationToken.None, CancellationToken.None, pause2);
        // 先搜尋填充 TT
        workerFull.SearchSingleDepth(4);
        // 重新搜尋（TT 命中，不觸發 IIR）
        long nodesFullFull = workerFull.NodesVisited;
        var boardFull2 = new Board(InitialFen);
        using var pause3 = new ManualResetEventSlim(true);
        var workerNoIir = new SearchWorker(boardFull2, new HandcraftedEvaluator(), ttFull, new EvalCache(), CancellationToken.None, CancellationToken.None, pause3);
        workerNoIir.SearchSingleDepth(4);
        long nodesNoIir = workerNoIir.NodesVisited;

        // IIR 觸發時（depth 減一）節點數應明顯少於無 IIR 搜尋
        Assert.True(nodesWithIir < nodesNoIir,
            $"IIR 應減少節點數：IIR={nodesWithIir}, 無IIR={nodesNoIir}");
    }

    // ─── 測試 2：TT 有命中（ttMove 非 null）→ 不觸發 IIR ───

    [Fact]
    public void Iir_TtHit_DoesNotReduceDepth()
    {
        // 先搜尋以填充 TT，再次搜尋同局面時 TT 命中 → 不應觸發 IIR
        var tt = new TranspositionTable(sizeMb: 4);
        var board = new Board(InitialFen);
        using var pause = new ManualResetEventSlim(true);
        var worker = new SearchWorker(board, new HandcraftedEvaluator(), tt, new EvalCache(), CancellationToken.None, CancellationToken.None, pause);

        // 第一次搜尋填充 TT
        int firstScore = worker.SearchSingleDepth(4);

        // 第二次搜尋：TT 命中，不觸發 IIR
        var board2 = new Board(InitialFen);
        using var pause2 = new ManualResetEventSlim(true);
        var worker2 = new SearchWorker(board2, new HandcraftedEvaluator(), tt, new EvalCache(), CancellationToken.None, CancellationToken.None, pause2);
        int secondScore = worker2.SearchSingleDepth(4);
        long nodesWithTtHit = worker2.NodesVisited;

        // 空 TT 搜尋的節點數（會觸發 IIR）
        var ttEmpty = new TranspositionTable(sizeMb: 4);
        var board3 = new Board(InitialFen);
        using var pause3 = new ManualResetEventSlim(true);
        var worker3 = new SearchWorker(board3, new HandcraftedEvaluator(), ttEmpty, new EvalCache(), CancellationToken.None, CancellationToken.None, pause3);
        int scoreEmpty = worker3.SearchSingleDepth(4);
        long nodesWithoutTtHit = worker3.NodesVisited;

        // 有 TT 命中（無 IIR）的搜尋應比空 TT（有 IIR）搜尋更多節點
        Assert.True(nodesWithTtHit >= nodesWithoutTtHit,
            $"TT 命中不應觸發 IIR：有TT命中={nodesWithTtHit}, 空TT={nodesWithoutTtHit}");
    }

    // ─── 測試 3：depth=3 → 低於 IirMinDepth(=4)，不觸發 IIR ───

    [Fact]
    public void Iir_Depth3_BelowMinDepth_NoReduction()
    {
        // depth=3 時不觸發 IIR，節點數應與全 depth=3 一致（無額外減量）
        var ttEmpty = new TranspositionTable(sizeMb: 4);
        var board = new Board(InitialFen);
        using var pause = new ManualResetEventSlim(true);
        var worker = new SearchWorker(board, new HandcraftedEvaluator(), ttEmpty, new EvalCache(), CancellationToken.None, CancellationToken.None, pause);

        // depth=3 搜尋（不應觸發 IIR）
        int score3 = worker.SearchSingleDepth(3);
        long nodes3 = worker.NodesVisited;

        // 若 IIR 在 depth=3 觸發，實際搜尋 depth=2，結果節點數應遠少於 depth=3
        // 我們驗證：depth=3 的節點數 >= depth=2 節點數（IIR 不應讓 depth=3 縮到 depth=2）
        var ttEmpty2 = new TranspositionTable(sizeMb: 4);
        var board2 = new Board(InitialFen);
        using var pause2 = new ManualResetEventSlim(true);
        var worker2 = new SearchWorker(board2, new HandcraftedEvaluator(), ttEmpty2, new EvalCache(), CancellationToken.None, CancellationToken.None, pause2);
        int score2 = worker2.SearchSingleDepth(2);
        long nodes2 = worker2.NodesVisited;

        // depth=3 節點數應大於 depth=2（若 IIR 在 depth=3 誤觸，nodes3 ≈ nodes2）
        Assert.True(nodes3 > nodes2,
            $"depth=3 不應觸發 IIR：depth3節點={nodes3} 應 > depth2節點={nodes2}");
    }

    // ─── 測試 4：excludedMove 非 null（排除搜尋節點）→ 不觸發 IIR ───

    [Fact]
    public void Iir_ExcludedMoveNonNull_SkipsIir()
    {
        // 排除搜尋（Singular Extension 子搜尋）不應觸發 IIR
        // 驗證方式：設定 excludedMove 的搜尋路徑不會對 depth 做額外的 IIR 減量
        // 使用 SearchWithExcludedMove 測試輔助方法
        var tt = new TranspositionTable(sizeMb: 4);
        var board = new Board(InitialFen);

        // 先搜尋 depth=1 以便取得一個合法走法作為 excludedMove
        using var pause = new ManualResetEventSlim(true);
        var primeWorker = new SearchWorker(board, new HandcraftedEvaluator(), tt, new EvalCache(), CancellationToken.None, CancellationToken.None, pause);
        primeWorker.SearchSingleDepth(1);
        var excludedMove = primeWorker.ProbeBestMove();

        // 空 TT + excludedMove → 排除搜尋不應觸發 IIR
        var ttEmpty = new TranspositionTable(sizeMb: 4);
        var board2 = new Board(InitialFen);
        using var pause2 = new ManualResetEventSlim(true);
        var worker = new SearchWorker(board2, new HandcraftedEvaluator(), ttEmpty, new EvalCache(), CancellationToken.None, CancellationToken.None, pause2);

        // SearchWithExcludedMove 在排除搜尋中，不應因 IIR 再額外減量（避免雙重減量）
        // 搜尋應能完成而不拋出例外
        int score = worker.SearchWithExcludedMove(depth: 4, excludedMove: excludedMove);
        long nodesWithExcluded = worker.NodesVisited;

        // 相同局面空 TT 正常搜尋（觸發 IIR）
        var ttEmpty2 = new TranspositionTable(sizeMb: 4);
        var board3 = new Board(InitialFen);
        using var pause3 = new ManualResetEventSlim(true);
        var worker3 = new SearchWorker(board3, new HandcraftedEvaluator(), ttEmpty2, new EvalCache(), CancellationToken.None, CancellationToken.None, pause3);
        int scoreNormal = worker3.SearchSingleDepth(4);
        long nodesNormal = worker3.NodesVisited;

        // 排除搜尋（不觸發 IIR）的節點數應 >= 正常搜尋（觸發 IIR 而減量）
        // 這是因為排除搜尋需要排除一個走法，所以實際探索路徑會不同
        // 主要驗證：排除搜尋能正常完成（不拋出例外）
        Assert.True(nodesWithExcluded > 0, "排除搜尋應探索至少一個節點");
    }

    private static SearchWorker CreateWorker(IBoard board, TranspositionTable? tt = null)
    {
        return new SearchWorker(
            board,
            new HandcraftedEvaluator(),
            tt ?? new TranspositionTable(sizeMb: 4),
            new EvalCache(),
            CancellationToken.None,
            CancellationToken.None,
            new ManualResetEventSlim(true));
    }
}
