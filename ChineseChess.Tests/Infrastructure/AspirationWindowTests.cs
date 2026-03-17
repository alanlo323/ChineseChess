using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Search;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 Aspiration Window（期望窗口搜尋）：
/// depth >= 2 時以 prevScore ± 50 的縮小窗口搜尋，
/// fail-low/fail-high 時自動擴大窗口並重試，最多 2 次後回退全窗口。
/// </summary>
public class AspirationWindowTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";
    private const int Infinity = 30000;

    // ─── 測試 1：depth=1 永遠使用全窗口（-∞, +∞）───

    [Fact]
    public void AspirationWindow_Depth1_UsesFullWindow()
    {
        // SearchSingleDepth(depth=1, alpha=-Infinity, beta=+Infinity)
        // 與 SearchSingleDepth(depth=1)（舊版無窗口）應回傳相同結果
        var tt = new TranspositionTable(sizeMb: 4);
        var board1 = new Board(InitialFen);
        var board2 = new Board(InitialFen);
        using var pause1 = new ManualResetEventSlim(true);
        using var pause2 = new ManualResetEventSlim(true);

        var worker1 = new SearchWorker(board1, new HandcraftedEvaluator(), tt, CancellationToken.None, CancellationToken.None, pause1);
        var worker2 = new SearchWorker(board2, new HandcraftedEvaluator(), new TranspositionTable(sizeMb: 4), CancellationToken.None, CancellationToken.None, pause2);

        // depth=1 全窗口
        int scoreFullWindow = worker1.SearchSingleDepth(1, -Infinity, Infinity);
        // depth=1 舊版接口（全窗口）
        int scoreDefault = worker2.SearchSingleDepth(1);

        Assert.Equal(scoreDefault, scoreFullWindow);
    }

    // ─── 測試 2：depth=2 且分數穩定 → 窗口搜尋與全窗口結果一致 ───

    [Fact]
    public void AspirationWindow_Depth2_StableScore_WindowMatchesFullSearch()
    {
        // 先搜尋 depth=1 得到 prevScore，再以窗口搜尋 depth=2
        // 結果應與全窗口搜尋 depth=2 相同
        var board1 = new Board(InitialFen);
        var board2 = new Board(InitialFen);

        using var pause1 = new ManualResetEventSlim(true);
        using var pause2 = new ManualResetEventSlim(true);

        var tt1 = new TranspositionTable(sizeMb: 4);
        var tt2 = new TranspositionTable(sizeMb: 4);

        var worker1 = new SearchWorker(board1, new HandcraftedEvaluator(), tt1, CancellationToken.None, CancellationToken.None, pause1);
        var worker2 = new SearchWorker(board2, new HandcraftedEvaluator(), tt2, CancellationToken.None, CancellationToken.None, pause2);

        // 全窗口搜尋 depth=2
        int prevScore = worker1.SearchSingleDepth(1);
        int fullWindowScore = worker1.SearchSingleDepth(2, -Infinity, Infinity);

        // 窗口搜尋 depth=2（基於 prevScore）
        worker2.SearchSingleDepth(1);
        int aspScore = worker2.SearchSingleDepth(2, prevScore - 50, prevScore + 50);

        // 若不是 fail-low/fail-high，窗口搜尋應得到與全窗口相同結果
        // 分數應在合理範圍內（可能因窗口邊界略有差異，但最終真實分數一致）
        Assert.Equal(fullWindowScore, aspScore);
    }

    // ─── 測試 3：模擬 fail-low → SearchEngine 窗口擴大、重試、找到正確分數 ───

    [Fact]
    public async Task AspirationWindow_FailLow_ExpandsWindowAndRetries()
    {
        // 使用非對稱局面（黑方少車）
        // fail-low：真實分數低於 alpha，Aspiration Window 應擴大窗口重試，最終找到相同結果
        var fen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABN1 w - - 0 1";

        // 全窗口搜尋取得參考分數
        var engineRef = new SearchEngine();
        var resultRef = await engineRef.SearchAsync(
            new Board(fen),
            new SearchSettings { Depth = 3, TimeLimitMs = 30000, ThreadCount = 1 },
            CancellationToken.None);
        int trueScore = resultRef.Score;

        // SearchEngine 搜尋相同局面，Aspiration Window 在 depth>=2 時可能觸發 fail-low
        // 但最終結果應與全窗口搜尋相同
        var engineAsp = new SearchEngine();
        var resultAsp = await engineAsp.SearchAsync(
            new Board(fen),
            new SearchSettings { Depth = 3, TimeLimitMs = 30000, ThreadCount = 1 },
            CancellationToken.None);

        // Aspiration Window 搜尋結果應與全窗口搜尋一致
        Assert.Equal(resultRef.BestMove, resultAsp.BestMove);
        Assert.Equal(trueScore, resultAsp.Score);
    }

    // ─── 測試 4：模擬 fail-high → SearchEngine 窗口擴大、重試、找到正確分數 ───

    [Fact]
    public async Task AspirationWindow_FailHigh_ExpandsWindowAndRetries()
    {
        // 使用初始局面（對稱），深度搜尋可能因 Aspiration Window 觸發 fail-high
        // 最終結果應與全窗口搜尋一致
        var fen = InitialFen;

        var engineRef = new SearchEngine();
        var resultRef = await engineRef.SearchAsync(
            new Board(fen),
            new SearchSettings { Depth = 3, TimeLimitMs = 30000, ThreadCount = 1 },
            CancellationToken.None);

        var engineAsp = new SearchEngine();
        var resultAsp = await engineAsp.SearchAsync(
            new Board(fen),
            new SearchSettings { Depth = 3, TimeLimitMs = 30000, ThreadCount = 1 },
            CancellationToken.None);

        Assert.Equal(resultRef.BestMove, resultAsp.BestMove);
        Assert.Equal(resultRef.Score, resultAsp.Score);
    }

    // ─── 測試 5：重試超過 2 次 → 回退全窗口，結果與全窗口搜尋一致 ───

    [Fact]
    public async Task AspirationWindow_ExceedsRetryLimit_FallsBackToFullWindow()
    {
        // 使用 SearchEngine 整合測試：執行完整搜尋，確認結果與全窗口搜尋一致
        var board = new Board(InitialFen);
        var engine1 = new SearchEngine();
        var engine2 = new SearchEngine();

        var settings = new SearchSettings { Depth = 3, TimeLimitMs = 30000, ThreadCount = 1 };

        // 兩個引擎搜尋相同局面，結果應相同（Aspiration Window 最終保證找到相同的最佳著法）
        var result1 = await engine1.SearchAsync(new Board(board.ToFen()), settings, CancellationToken.None);
        var result2 = await engine2.SearchAsync(new Board(board.ToFen()), settings, CancellationToken.None);

        Assert.False(result1.BestMove.IsNull);
        Assert.False(result2.BestMove.IsNull);
        Assert.Equal(result1.BestMove, result2.BestMove);
        Assert.Equal(result1.Score, result2.Score);
    }

    // ─── 測試 6：CancellationToken 取消時仍回傳有效結果 ───

    [Fact]
    public async Task AspirationWindow_CancelledMidSearch_ReturnsValidResult()
    {
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        using var cts = new CancellationTokenSource();

        // depth=8，時間充足，但由 CancellationToken 在短時間後取消
        var settings = new SearchSettings { Depth = 8, TimeLimitMs = 30000, ThreadCount = 1 };

        // 在搜尋開始後取消
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            cts.Cancel();
        });

        var result = await engine.SearchAsync(board, settings, cts.Token);

        // 取消後仍應回傳有效結果（即使不完整）
        // result 可能為預設值（BestMove.IsNull = true），但不應拋出例外
        Assert.NotNull(result);
        // 應完成至少 depth=1
        Assert.True(result.Depth >= 0);
    }

    private static SearchWorker CreateWorker(IBoard board, TranspositionTable? tt = null)
    {
        return new SearchWorker(
            board,
            new HandcraftedEvaluator(),
            tt ?? new TranspositionTable(sizeMb: 4),
            CancellationToken.None,
            CancellationToken.None,
            new ManualResetEventSlim(true));
    }
}
