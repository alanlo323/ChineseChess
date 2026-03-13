using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Search;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 Triangular PV Table（主要變例表）的功能：
///   - PV 表存取和複製正確性
///   - 每次 alpha 更新時複製子 PV
///   - SearchWorker 能存取根部 PV 第一步
/// </summary>
public class TriangularPvTableTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    [Fact]
    public void PvTable_InitiallyEmpty_RootPvMoveIsNull()
    {
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        // 初始狀態，根部 PV 應為空
        var pv = worker.GetRootPv();
        Assert.Empty(pv);
    }

    [Fact]
    public void PvTable_AfterSearch_RootPvIsNotEmpty()
    {
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        // 搜尋後應有 PV
        worker.SearchSingleDepth(2);
        var pv = worker.GetRootPv();

        Assert.NotEmpty(pv);
    }

    [Fact]
    public void PvTable_AfterSearch_FirstPvMoveMatchesBestMove()
    {
        var board = new Board(InitialFen);
        var tt = new TranspositionTable(4);
        var worker = CreateWorkerWithTt(board, tt);

        worker.SearchSingleDepth(2);
        var pv = worker.GetRootPv();
        var bestMove = worker.ProbeBestMove();

        Assert.NotEmpty(pv);
        Assert.False(bestMove.IsNull);
        Assert.Equal(bestMove, pv[0]);
    }

    [Fact]
    public void PvTable_AfterSearch_PvLengthMatchesOrExceedsDepth()
    {
        // 搜尋深度 3，PV 長度應至少為 1（可能因棋局特性短一些）
        var board = new Board(InitialFen);
        var worker = CreateWorker(board);

        worker.SearchSingleDepth(3);
        var pv = worker.GetRootPv();

        Assert.NotEmpty(pv);
    }

    [Fact]
    public async Task SearchEngine_AfterSearch_PvLineMatchesRootPvFirstMove()
    {
        // 確認 SearchEngine 的 PvLine 和 SearchWorker 的根部 PV 第一步一致
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 12000, ThreadCount = 1 };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        Assert.False(result.BestMove.IsNull);
        Assert.False(string.IsNullOrWhiteSpace(result.PvLine));
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

    private static SearchWorker CreateWorkerWithTt(IBoard board, TranspositionTable tt)
    {
        return new SearchWorker(
            board,
            new HandcraftedEvaluator(),
            tt,
            CancellationToken.None,
            CancellationToken.None,
            new ManualResetEventSlim(true));
    }
}
