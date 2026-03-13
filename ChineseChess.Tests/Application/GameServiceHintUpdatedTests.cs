using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Application;

/// <summary>
/// 測試提示功能實時顯示最佳著法（HintUpdated 事件）的 TDD 測試套件。
/// 驗證 Phase 1（SearchProgress 擴充）、Phase 2（HintUpdated 事件與 IsHintSearching）。
/// </summary>
public class GameServiceHintUpdatedTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── Phase 1：SearchProgress 擴充 ──────────────────────────────────────

    [Fact]
    public async Task SearchProgress_ShouldContainBestMoveCoordinates_WhenBestMoveExists()
    {
        // 驗證 SearchProgress 報告中包含 BestMoveFrom / BestMoveTo 座標
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 5000, ThreadCount = 1 };

        var nonHeartbeatReports = new List<SearchProgress>();
        var progress = new Progress<SearchProgress>(p =>
        {
            if (!p.IsHeartbeat)
            {
                nonHeartbeatReports.Add(p);
            }
        });

        await engine.SearchAsync(board, settings, CancellationToken.None, progress);

        // 至少有一個非心跳報告，且包含有效的座標
        Assert.NotEmpty(nonHeartbeatReports);
        Assert.Contains(nonHeartbeatReports, p => p.BestMoveFrom >= 0 && p.BestMoveTo >= 0);
        Assert.All(nonHeartbeatReports.FindAll(p => p.BestMoveFrom >= 0), p =>
        {
            Assert.NotEqual(p.BestMoveFrom, p.BestMoveTo); // 起點和終點不同
            Assert.InRange(p.BestMoveFrom, 0, 89);
            Assert.InRange(p.BestMoveTo, 0, 89);
        });
    }

    [Fact]
    public async Task SearchProgress_HeartbeatReport_ShouldHaveMinusOneCoordinates_WhenNoBestMoveYet()
    {
        // 驗證心跳報告在無最佳著法時的預設座標為 -1
        var board = new Board(InitialFen);
        var engine = new SearchEngine();
        // 深度 1 很快完成，使用高深度+長時間確保心跳先於結果
        var settings = new SearchSettings { Depth = 10, TimeLimitMs = 500, ThreadCount = 1 };

        var firstHeartbeat = (SearchProgress?)null;
        var tcs = new TaskCompletionSource<SearchProgress>();

        var progress = new Progress<SearchProgress>(p =>
        {
            if (p.IsHeartbeat && !tcs.Task.IsCompleted)
            {
                tcs.TrySetResult(p);
            }
        });

        // 不等搜尋完成，只取第一個心跳
        var searchTask = engine.SearchAsync(board, settings, CancellationToken.None, progress);
        firstHeartbeat = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await searchTask; // 等待完整搜尋完成

        // 初始心跳（搜尋尚未完成任何深度）時 BestMoveFrom/To 預設 -1
        Assert.NotNull(firstHeartbeat);
        Assert.Equal(-1, firstHeartbeat.BestMoveFrom);
        Assert.Equal(-1, firstHeartbeat.BestMoveTo);
    }

    // ─── Phase 2：HintUpdated 事件 ─────────────────────────────────────────

    [Fact]
    public async Task HintUpdated_ShouldFireAtLeastOnce_DuringHintSearch()
    {
        // 提示搜尋期間 HintUpdated 應至少觸發一次
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(depth: 5, timeMs: 5000, threadCount: 1);
        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        var hintUpdatedResults = new List<SearchResult>();
        gameService.HintUpdated += result => hintUpdatedResults.Add(result);

        await gameService.GetHintAsync();

        Assert.NotEmpty(hintUpdatedResults);
        Assert.All(hintUpdatedResults, r =>
        {
            Assert.False(r.BestMove.IsNull);
            Assert.True(r.Depth >= 2);
        });
    }

    [Fact]
    public async Task HintReady_ShouldFireExactlyOnce_AfterHintSearchCompletes()
    {
        // 搜尋完成後 HintReady 應觸發一次
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(depth: 3, timeMs: 5000, threadCount: 1);
        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        var hintReadyCount = 0;
        gameService.HintReady += _ => hintReadyCount++;

        await gameService.GetHintAsync();

        Assert.Equal(1, hintReadyCount);
    }

    [Fact]
    public async Task HintUpdated_ShouldNotFire_WhenAiIsPlayingMove()
    {
        // AI 對弈模式（applyBestMove: true）下不應觸發 HintUpdated
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(depth: 2, timeMs: 3000, threadCount: 1);

        var hintUpdatedFired = false;
        gameService.HintUpdated += _ => hintUpdatedFired = true;

        // PlayerVsAi 模式下 AI 回應玩家著法時走的是 applyBestMove: true
        await gameService.StartGameAsync(GameMode.PlayerVsAi);
        var legalMoves = gameService.CurrentBoard.GenerateLegalMoves().ToList();
        await gameService.HumanMoveAsync(legalMoves[0]);

        // 等待 AI 完成思考
        var waitCount = 0;
        while (gameService.IsThinking && waitCount < 500)
        {
            await Task.Delay(10);
            waitCount++;
        }

        Assert.False(hintUpdatedFired);
    }

    [Fact]
    public async Task IsHintSearching_ShouldBeTrue_DuringHintSearch()
    {
        // IsHintSearching 在搜尋中應為 true
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(depth: 18, timeMs: 5000, threadCount: 1);
        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        var wasSearchingDuringHint = false;
        gameService.HintUpdated += _ =>
        {
            if (gameService.IsHintSearching)
            {
                wasSearchingDuringHint = true;
            }
        };

        var hintTask = gameService.GetHintAsync();
        await Task.Delay(50); // 等搜尋開始

        Assert.True(gameService.IsHintSearching);

        await hintTask;
    }

    [Fact]
    public async Task IsHintSearching_ShouldBeFalse_AfterHintSearchCompletes()
    {
        // IsHintSearching 在搜尋完成後應為 false
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(depth: 3, timeMs: 3000, threadCount: 1);
        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        await gameService.GetHintAsync();

        Assert.False(gameService.IsHintSearching);
    }

    [Fact]
    public async Task HintUpdated_ShouldContainValidBestMove_WithDepthAtLeast2()
    {
        // HintUpdated 事件中的結果應有有效著法且深度 >= 2
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(depth: 5, timeMs: 5000, threadCount: 1);
        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        var firstResult = (SearchResult?)null;
        var tcs = new TaskCompletionSource<SearchResult>();
        gameService.HintUpdated += result =>
        {
            if (!tcs.Task.IsCompleted)
            {
                tcs.TrySetResult(result);
            }
        };

        var hintTask = gameService.GetHintAsync();
        firstResult = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await hintTask;

        Assert.NotNull(firstResult);
        Assert.False(firstResult.BestMove.IsNull);
        Assert.True(firstResult.Depth >= 2);
        Assert.InRange(firstResult.BestMove.From, 0, 89);
        Assert.InRange(firstResult.BestMove.To, 0, 89);
    }

    [Fact]
    public async Task IsHintSearching_ShouldBeFalse_BeforeHintSearch()
    {
        // 未進行提示搜尋時 IsHintSearching 應為 false
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        Assert.False(gameService.IsHintSearching);
    }

    [Fact]
    public async Task HintUpdated_ShouldNotFire_InAiVsAiMode()
    {
        // AiVsAi 模式下 AI 走棋不觸發 HintUpdated
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(depth: 1, timeMs: 1000, threadCount: 1);

        var hintUpdatedFired = false;
        gameService.HintUpdated += _ => hintUpdatedFired = true;

        await gameService.StartGameAsync(GameMode.AiVsAi);

        // 等待第一步 AI 完成
        var waitCount = 0;
        while (gameService.IsThinking && waitCount < 500)
        {
            await Task.Delay(10);
            waitCount++;
        }

        Assert.False(hintUpdatedFired);
    }
}
