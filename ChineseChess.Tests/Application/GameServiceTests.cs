using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Application;

public class GameServiceTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    [Fact]
    public async Task Hint_ShouldMatchDirectSearchResultInPlayerVsAiMode()
    {
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 3000, ThreadCount = 1 };
        var directResult = await engine.SearchAsync(new Board(InitialFen), settings, CancellationToken.None);

        var gameService = new GameService(engine);
        gameService.SetDifficulty(settings.Depth, settings.TimeLimitMs, settings.ThreadCount);
        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        var hint = await gameService.GetHintAsync();

        Assert.False(hint.BestMove.IsNull);
        Assert.Equal(directResult.BestMove, hint.BestMove);
    }

    [Fact]
    public async Task AiVsAiFirstMove_ShouldMatchDirectSearchResult()
    {
        var engine = new SearchEngine();
        var settings = new SearchSettings { Depth = 2, TimeLimitMs = 3000, ThreadCount = 1 };
        var directResult = await engine.SearchAsync(new Board(InitialFen), settings, CancellationToken.None);

        var expectedBoard = new Board(InitialFen);
        expectedBoard.MakeMove(directResult.BestMove);

        var gameService = new GameService(engine);
        gameService.SetDifficulty(settings.Depth, settings.TimeLimitMs, settings.ThreadCount);
        await gameService.StartGameAsync(GameMode.AiVsAi);

        Assert.Equal(expectedBoard.ToFen(), gameService.CurrentBoard.ToFen());
    }

    [Fact]
    public async Task PauseThinking_ShouldPauseAndResumeHintSearch()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(18, 10000, 1);
        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        var hintTask = gameService.GetHintAsync();
        await Task.Delay(40);
        await gameService.PauseThinkingAsync();

        await gameService.ResumeThinkingAsync();
        var hint = await hintTask;

        Assert.False(hint.BestMove.IsNull);
    }

    [Fact]
    public async Task StopThinking_ShouldCancelHintSearch()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(18, 10000, 1);
        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        var hintTask = gameService.GetHintAsync();
        await Task.Delay(40);
        await gameService.StopGameAsync();
        var hint = await hintTask;

        Assert.False(gameService.IsThinking);
        Assert.NotNull(hint);
    }
}
