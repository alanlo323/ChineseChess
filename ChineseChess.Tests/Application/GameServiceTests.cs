using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Search;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

    [Fact]
    public async Task ExportTranspositionTable_ShouldCreateFile_WhenIdle()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        gameService.SetDifficulty(1, 3000, 1);
        await gameService.StartGameAsync(GameMode.PlayerVsAi);

        var filePath = Path.Combine(Path.GetTempPath(), $"tt-export-{Guid.NewGuid():N}.cctt");

        try
        {
            await gameService.ExportTranspositionTableAsync(filePath, asJson: false);
            Assert.True(File.Exists(filePath));
            Assert.True(new FileInfo(filePath).Length > 0);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task ImportTranspositionTable_ShouldReportFailure_OnInvalidFile()
    {
        var engine = new SearchEngine();
        var gameService = new GameService(engine);
        var messages = new List<string>();
        Action<string> handler = msg => messages.Add(msg);
        gameService.ThinkingProgress += handler;

        var filePath = Path.Combine(Path.GetTempPath(), $"tt-invalid-{Guid.NewGuid():N}.cctt");
        await File.WriteAllTextAsync(filePath, "not valid tt", Encoding.UTF8);
        await gameService.ImportTranspositionTableAsync(filePath, asJson: false);
        gameService.ThinkingProgress -= handler;

        Assert.Contains(messages, msg => msg.Contains("TT 匯入失敗"));

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ImportTranspositionTable_ShouldRestoreUsableCache()
    {
        var sourceEngine = new SearchEngine();
        var targetService = new GameService(new SearchEngine());
        var filePath = Path.Combine(Path.GetTempPath(), $"tt-import-{Guid.NewGuid():N}.cctt");

        await sourceEngine.SearchAsync(
            new Board(InitialFen),
            new SearchSettings { Depth = 1, TimeLimitMs = 500, ThreadCount = 1 },
            CancellationToken.None);

        using (var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await sourceEngine.ExportTranspositionTableAsync(file, asJson: false, ct: CancellationToken.None);
        }

        try
        {
            await targetService.ImportTranspositionTableAsync(filePath, asJson: false);
            await targetService.StartGameAsync(GameMode.PlayerVsAi);
            var hint = await targetService.GetHintAsync();
            Assert.False(hint.BestMove.IsNull);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
