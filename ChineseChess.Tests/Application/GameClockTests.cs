using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Application;

/// <summary>
/// 驗證 GameClock 棋鐘機制：
///   狀態機轉換、計時正確性、暫停/恢復、超時觸發、停止後不計時。
/// </summary>
public class GameClockTests
{
    // ─── 測試 1：Start() 後正確方開始計時 ───

    [Fact]
    public void Start_WithRedFirstPlayer_RedTimerIsRunning()
    {
        // 使用假時鐘（可控制時間）
        var fakeNow = new FakeNowProvider(DateTime.UtcNow);
        var clock = new GameClock(TimeSpan.FromMinutes(10), fakeNow.GetNow);

        clock.Start(PieceColor.Red);

        Assert.True(clock.IsRunning);
        Assert.Equal(PieceColor.Red, clock.ActivePlayer);

        // 推進 30 秒
        fakeNow.Advance(TimeSpan.FromSeconds(30));

        // 紅方應減少 30 秒
        Assert.True(clock.RedRemaining < TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), clock.BlackRemaining); // 黑方未計時
    }

    // ─── 測試 2：SwitchTurn() 正確切換計時方 ───

    [Fact]
    public void SwitchTurn_AfterRedMoves_BlackStartsTiming()
    {
        var fakeNow = new FakeNowProvider(DateTime.UtcNow);
        var clock = new GameClock(TimeSpan.FromMinutes(10), fakeNow.GetNow);

        clock.Start(PieceColor.Red);
        fakeNow.Advance(TimeSpan.FromSeconds(10));

        clock.SwitchTurn(); // 紅方走完，換黑方

        fakeNow.Advance(TimeSpan.FromSeconds(20));

        // 紅方消耗了 10 秒
        var redRemaining = clock.RedRemaining;
        Assert.True(Math.Abs((redRemaining - TimeSpan.FromSeconds(590)).TotalMilliseconds) < 100,
            $"紅方剩餘時間應約為 590 秒，實際 {redRemaining.TotalSeconds:F1}");

        // 黑方消耗了 20 秒
        var blackRemaining = clock.BlackRemaining;
        Assert.True(Math.Abs((blackRemaining - TimeSpan.FromSeconds(580)).TotalMilliseconds) < 100,
            $"黑方剩餘時間應約為 580 秒，實際 {blackRemaining.TotalSeconds:F1}");

        Assert.Equal(PieceColor.Black, clock.ActivePlayer);
    }

    // ─── 測試 3：Pause() + Resume() → 暫停期間時間不流失 ───

    [Fact]
    public void Pause_Resume_PausedDurationNotCounted()
    {
        var fakeNow = new FakeNowProvider(DateTime.UtcNow);
        var clock = new GameClock(TimeSpan.FromMinutes(10), fakeNow.GetNow);

        clock.Start(PieceColor.Red);

        // 計時 5 秒後暫停
        fakeNow.Advance(TimeSpan.FromSeconds(5));
        clock.Pause();

        // 暫停 30 秒（不應被計算）
        fakeNow.Advance(TimeSpan.FromSeconds(30));

        // 恢復後再計時 3 秒
        clock.Resume();
        fakeNow.Advance(TimeSpan.FromSeconds(3));

        // 總消耗應為 5 + 3 = 8 秒（暫停的 30 秒不計）
        var redRemaining = clock.RedRemaining;
        Assert.True(Math.Abs((redRemaining - TimeSpan.FromSeconds(592)).TotalMilliseconds) < 100,
            $"暫停期間不應計入：剩餘應約 592 秒，實際 {redRemaining.TotalSeconds:F1}");
    }

    // ─── 測試 4：時間耗盡 → 觸發 OnTimeout(color) ───

    [Fact]
    public void Timeout_WhenTimeExhausted_FiresOnTimeoutEvent()
    {
        var fakeNow = new FakeNowProvider(DateTime.UtcNow);
        // 設定 1 秒的時間（方便測試）
        var clock = new GameClock(TimeSpan.FromSeconds(1), fakeNow.GetNow);

        PieceColor? timedOutColor = null;
        clock.OnTimeout += (_, color) => timedOutColor = color;

        clock.Start(PieceColor.Red);

        // 推進超過時間上限
        fakeNow.Advance(TimeSpan.FromSeconds(2));
        clock.Tick(); // 手動觸發計時器更新

        Assert.Equal(PieceColor.Red, timedOutColor);
    }

    // ─── 測試 5：Stop() 後不再計時 ───

    [Fact]
    public void Stop_AfterStop_ClockNoLongerRuns()
    {
        var fakeNow = new FakeNowProvider(DateTime.UtcNow);
        var clock = new GameClock(TimeSpan.FromMinutes(10), fakeNow.GetNow);

        clock.Start(PieceColor.Red);
        fakeNow.Advance(TimeSpan.FromSeconds(5));
        clock.Stop();

        var redRemainingAfterStop = clock.RedRemaining;

        // 停止後推進時間，剩餘時間不應再減少
        fakeNow.Advance(TimeSpan.FromSeconds(10));

        Assert.False(clock.IsRunning);
        Assert.Null(clock.ActivePlayer);
        Assert.Equal(redRemainingAfterStop, clock.RedRemaining);
    }

    // ─── 測試 6：非限時模式下 Clock 為 null，GameService 行為不變 ───

    [Fact]
    public async Task GameService_NonTimedMode_ClockIsNull()
    {
        // 使用預設設定（非限時模式）啟動遊戲，Clock 屬性應為 null
        var mockEngine = new MockAiEngine();
        var service = new GameService(mockEngine);

        await service.StartGameAsync(ChineseChess.Application.Enums.GameMode.PlayerVsAi);

        Assert.Null(service.Clock);
    }
}

/// <summary>
/// 假時鐘提供者：允許測試控制「現在時間」，不依賴系統時鐘。
/// </summary>
internal sealed class FakeNowProvider
{
    private DateTime current;

    public FakeNowProvider(DateTime initial)
    {
        current = initial;
    }

    public DateTime GetNow() => current;

    public void Advance(TimeSpan duration)
    {
        current = current.Add(duration);
    }
}

/// <summary>
/// Mock AI 引擎：用於 GameService 整合測試。
/// </summary>
internal sealed class MockAiEngine : ChineseChess.Application.Interfaces.IAiEngine
{
    public Task<ChineseChess.Application.Interfaces.SearchResult> SearchAsync(
        ChineseChess.Domain.Entities.IBoard board,
        ChineseChess.Application.Interfaces.SearchSettings settings,
        CancellationToken ct = default,
        IProgress<ChineseChess.Application.Interfaces.SearchProgress>? progress = null)
    {
        var result = new ChineseChess.Application.Interfaces.SearchResult();
        var moves = board.GenerateLegalMoves().GetEnumerator();
        if (moves.MoveNext()) result.BestMove = moves.Current;
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ChineseChess.Application.Interfaces.MoveEvaluation>> EvaluateMovesAsync(
        ChineseChess.Domain.Entities.IBoard board,
        IEnumerable<ChineseChess.Domain.Entities.Move> moves,
        int depth,
        CancellationToken ct = default,
        IProgress<string>? progress = null)
        => Task.FromResult<IReadOnlyList<ChineseChess.Application.Interfaces.MoveEvaluation>>(
            new List<ChineseChess.Application.Interfaces.MoveEvaluation>());

    public Task ExportTranspositionTableAsync(System.IO.Stream output, bool asJson, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ImportTranspositionTableAsync(System.IO.Stream input, bool asJson, CancellationToken ct = default)
        => Task.CompletedTask;

    public ChineseChess.Application.Interfaces.TTStatistics GetTTStatistics()
        => new ChineseChess.Application.Interfaces.TTStatistics();

    public IAiEngine CloneWithCopiedTT() => new MockAiEngine();
    public IAiEngine CloneWithEmptyTT() => new MockAiEngine();
    public void MergeTranspositionTableFrom(IAiEngine other) { }

    public IEnumerable<ChineseChess.Application.Interfaces.TTEntry> EnumerateTTEntries()
        => Array.Empty<ChineseChess.Application.Interfaces.TTEntry>();

    public ChineseChess.Application.Interfaces.TTTreeNode? ExploreTTTree(
        ChineseChess.Domain.Entities.IBoard board, int maxDepth = 6) => null;
}
