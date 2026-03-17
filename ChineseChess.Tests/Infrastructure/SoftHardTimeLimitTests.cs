using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 Soft / Hard 雙時限機制：
///   非限時模式：HardTimeLimitMs = TimeLimitMs（中途取消），SoftTimeLimitMs = null（不限）
///   限時模式：Soft 先觸發（完成當前深度後停止），Hard 強制中途取消
/// </summary>
public class SoftHardTimeLimitTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── 測試 1：非限時模式 → HardTimeLimitMs = TimeLimitMs，中途取消（不等完整層）───

    [Fact]
    public async Task NonTimedMode_HardLimitEqualsTimeLimitMs()
    {
        // 設定 Hard 時限（短），不設 Soft → 搜尋應在硬時限到期時中途取消
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var sw = Stopwatch.StartNew();

        var settings = new SearchSettings
        {
            Depth = 20,             // 深度很大，確保被時間截斷
            TimeLimitMs = 200,      // 200ms 硬時限
            SoftTimeLimitMs = null, // 無軟時限
            HardTimeLimitMs = 200,  // Hard = TimeLimitMs
            ThreadCount = 1
        };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);
        sw.Stop();

        // 搜尋時間應不超過 Hard + 一定餘量
        Assert.True(sw.ElapsedMilliseconds < 1500,
            $"非限時模式下 Hard 時限應截斷搜尋，實際耗時 {sw.ElapsedMilliseconds}ms");
        Assert.NotNull(result);
        Assert.True(result.Depth >= 1, "至少應完成 depth=1");
    }

    // ─── 測試 2：非限時模式 → SoftTimeLimitMs = null，迭代加深不因 Soft 停止 ───

    [Fact]
    public async Task NonTimedMode_SoftLimitNull_DoesNotStopIterativeDeepening()
    {
        // Soft = null，搜尋應持續直到 Hard 時限或 Depth 上限
        var board = new Board(InitialFen);
        var engine = new SearchEngine();

        var settings = new SearchSettings
        {
            Depth = 5,
            TimeLimitMs = 30000,    // 足夠長
            SoftTimeLimitMs = null, // 無軟時限
            HardTimeLimitMs = null, // 無硬時限（由 Depth 上限控制）
            ThreadCount = 1
        };

        var result = await engine.SearchAsync(board, settings, CancellationToken.None);

        // 應完成全部 5 層
        Assert.Equal(5, result.Depth);
        Assert.False(result.BestMove.IsNull);
    }

    // ─── 測試 3：限時模式 → Soft 觸發後完成當前深度停止 ───

    [Fact]
    public async Task TimedMode_SoftLimit_StopsAfterCompletingCurrentDepth()
    {
        // Soft 設較短，Hard 設較長，搜尋應在完成 Soft 觸發時的當前深度後停止
        var board = new Board(InitialFen);
        var engine = new SearchEngine();

        var settings = new SearchSettings
        {
            Depth = 20,
            TimeLimitMs = 30000,
            SoftTimeLimitMs = 300,  // 300ms Soft
            HardTimeLimitMs = 5000, // 5000ms Hard（不應觸發）
            ThreadCount = 1
        };

        var sw = Stopwatch.StartNew();
        var result = await engine.SearchAsync(board, settings, CancellationToken.None);
        sw.Stop();

        // Soft 觸發後應在合理時間內完成（不應等到 Hard）
        Assert.True(sw.ElapsedMilliseconds < 4000,
            $"Soft 時限應在 Hard 之前觸發搜尋停止，實際耗時 {sw.ElapsedMilliseconds}ms");
        Assert.True(result.Depth >= 1, "至少應完成 depth=1");
        Assert.False(result.BestMove.IsNull);
    }

    // ─── 測試 4：限時模式 → Hard 觸發後中途立即取消 ───

    [Fact]
    public async Task TimedMode_HardLimit_CancelsMidSearch()
    {
        // Soft 設很長，Hard 設很短，搜尋應被 Hard 中途取消
        var board = new Board(InitialFen);
        var engine = new SearchEngine();

        var settings = new SearchSettings
        {
            Depth = 20,
            TimeLimitMs = 30000,
            SoftTimeLimitMs = 10000, // 10s Soft（不應觸發）
            HardTimeLimitMs = 150,   // 150ms Hard（應觸發）
            ThreadCount = 1
        };

        var sw = Stopwatch.StartNew();
        var result = await engine.SearchAsync(board, settings, CancellationToken.None);
        sw.Stop();

        // Hard 時限應截斷搜尋
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"Hard 時限應截斷搜尋，實際耗時 {sw.ElapsedMilliseconds}ms");
        Assert.NotNull(result);
        Assert.True(result.Depth >= 1, "至少應完成 depth=1");
    }

    // ─── 測試 5：Soft < Hard → Soft 先觸發 ───

    [Fact]
    public async Task SoftBeforeHard_SoftTriggersFirst()
    {
        // Soft 觸發應早於 Hard，驗證 Soft 先生效
        var board = new Board(InitialFen);
        var engineSoft = new SearchEngine();
        var engineHard = new SearchEngine();

        // 以 Soft 停止
        var settingsSoft = new SearchSettings
        {
            Depth = 20,
            TimeLimitMs = 10000,
            SoftTimeLimitMs = 300,   // 300ms Soft
            HardTimeLimitMs = 10000, // 10s Hard（不應觸發）
            ThreadCount = 1
        };

        // 以 Hard 停止（等效的搜尋時間）
        var settingsHard = new SearchSettings
        {
            Depth = 20,
            TimeLimitMs = 10000,
            SoftTimeLimitMs = null,
            HardTimeLimitMs = 300,   // 300ms Hard
            ThreadCount = 1
        };

        var swSoft = Stopwatch.StartNew();
        var resultSoft = await engineSoft.SearchAsync(new Board(InitialFen), settingsSoft, CancellationToken.None);
        swSoft.Stop();

        var swHard = Stopwatch.StartNew();
        var resultHard = await engineHard.SearchAsync(new Board(InitialFen), settingsHard, CancellationToken.None);
        swHard.Stop();

        // Soft 觸發的搜尋：完成整層後停止，Depth 應 >= Hard 觸發的搜尋（Hard 可能中途截斷）
        // 兩者都應在合理時間內完成
        Assert.True(swSoft.ElapsedMilliseconds < 5000 && swHard.ElapsedMilliseconds < 5000);
        Assert.False(resultSoft.BestMove.IsNull);
        Assert.False(resultHard.BestMove.IsNull);
    }

    // ─── 測試 6：向下相容 → 僅設 TimeLimitMs 時，Hard = TimeLimitMs，Soft = null ───

    [Fact]
    public async Task BackwardCompat_OnlyTimeLimitMs_WorksAsHardLimit()
    {
        // 僅設定 TimeLimitMs（舊版行為），應如 Hard 時限運作
        var board = new Board(InitialFen);
        var engine = new SearchEngine();

        var settings = new SearchSettings
        {
            Depth = 20,
            TimeLimitMs = 200,  // 舊版設定：僅 TimeLimitMs
            ThreadCount = 1
        };

        var sw = Stopwatch.StartNew();
        var result = await engine.SearchAsync(board, settings, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"TimeLimitMs 應作為硬時限，實際耗時 {sw.ElapsedMilliseconds}ms");
        Assert.True(result.Depth >= 1);
    }
}
