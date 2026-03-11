using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Application;

public class GameServiceHintExplanationTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    [Fact]
    public async Task ExplainLatestHintAsync_ShouldCallService_WhenHintExists()
    {
        var engine = new SearchEngine();
        var explanationService = new StubHintExplanationService();
        var gameService = new GameService(engine, explanationService);
        gameService.SetDifficulty(1, 500, 1);

        await gameService.StartGameAsync(ChineseChess.Application.Enums.GameMode.PlayerVsAi);
        await gameService.GetHintAsync();

        var explanation = await gameService.ExplainLatestHintAsync();

        Assert.Equal("這步看起來是目前最佳選擇。", explanation);
        Assert.Equal(1, explanationService.CallCount);
        Assert.NotNull(explanationService.LatestRequest);
        Assert.Equal(InitialFen, explanationService.LatestRequest.Fen);
        Assert.Equal(PieceColor.Red, explanationService.LatestRequest.SideToMove);
        Assert.False(string.IsNullOrWhiteSpace(explanationService.LatestRequest.ThinkingTree));
    }

    [Fact]
    public async Task ExplainLatestHintAsync_ShouldReturnMessage_WhenNoHintAvailable()
    {
        var engine = new SearchEngine();
        var explanationService = new StubHintExplanationService();
        var gameService = new GameService(engine, explanationService);

        await gameService.StartGameAsync(ChineseChess.Application.Enums.GameMode.PlayerVsAi);

        var explanation = await gameService.ExplainLatestHintAsync();

        Assert.Equal(0, explanationService.CallCount);
        Assert.Contains("請先按提示走法", explanation);
    }

    [Fact]
    public async Task ExplainLatestHintAsync_ShouldReject_WhenBoardChangedAfterHint()
    {
        var engine = new SearchEngine();
        var explanationService = new StubHintExplanationService();
        var gameService = new GameService(engine, explanationService);
        gameService.SetDifficulty(1, 500, 1);

        await gameService.StartGameAsync(ChineseChess.Application.Enums.GameMode.PlayerVsAi);
        await gameService.GetHintAsync();

        ((Board)gameService.CurrentBoard).ParseFen("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");

        var explanation = await gameService.ExplainLatestHintAsync();

        Assert.Equal(0, explanationService.CallCount);
        Assert.Contains("局面已更新", explanation);
    }

    [Fact]
    public async Task ExplainLatestHintAsync_ShouldReturnFriendlyMessage_WhenServiceThrows()
    {
        var engine = new SearchEngine();
        var explanationService = new StubHintExplanationService
        {
            ResponseFactory = () => throw new InvalidOperationException("mocked failure")
        };
        var gameService = new GameService(engine, explanationService);
        gameService.SetDifficulty(1, 500, 1);

        await gameService.StartGameAsync(ChineseChess.Application.Enums.GameMode.PlayerVsAi);
        await gameService.GetHintAsync();

        var explanation = await gameService.ExplainLatestHintAsync();

        Assert.Equal(1, explanationService.CallCount);
        Assert.Contains("mocked failure", explanation);
    }

    private sealed class StubHintExplanationService : IHintExplanationService
    {
        public int CallCount { get; private set; }
        public HintExplanationRequest? LatestRequest { get; private set; }
        public Func<string> ResponseFactory { get; set; } = () => "這步看起來是目前最佳選擇。";

        public Task<string> ExplainAsync(HintExplanationRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            CallCount++;
            LatestRequest = request;
            return Task.FromResult(ResponseFactory());
        }
    }
}
