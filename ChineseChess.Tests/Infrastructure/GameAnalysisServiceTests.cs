using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Analysis;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

public class GameAnalysisServiceTests
{
    private const string TestFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── 測試 1: GameAnalysisSettings 預設值正確 ──────────────────────────

    [Fact]
    public void GameAnalysisSettings_ShouldHaveCorrectDefaults()
    {
        var settings = new GameAnalysisSettings();

        Assert.True(settings.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(settings.SystemPrompt));
        Assert.False(string.IsNullOrWhiteSpace(settings.Disclaimer));
        Assert.True(settings.MaxTokens > 0);
        Assert.True(settings.TimeoutSeconds > 0);
        Assert.Equal(0, settings.Temperature);
        Assert.False(settings.EnableReasoning);
    }

    // ─── 測試 2: GameAnalysisRequest 建構正確 ───────────────────────────

    [Fact]
    public void GameAnalysisRequest_ShouldBuildWithAllFields()
    {
        var request = new GameAnalysisRequest
        {
            Fen = TestFen,
            MovedBy = PieceColor.Red,
            LastMoveNotation = "車一進一",
            Score = 150,
            SearchDepth = 8,
            Nodes = 500000,
            PrincipalVariation = "車一進一 馬8進7",
            MoveNumber = 3
        };

        Assert.Equal(TestFen, request.Fen);
        Assert.Equal(PieceColor.Red, request.MovedBy);
        Assert.Equal("車一進一", request.LastMoveNotation);
        Assert.Equal(150, request.Score);
        Assert.Equal(8, request.SearchDepth);
        Assert.Equal(500000, request.Nodes);
        Assert.Equal("車一進一 馬8進7", request.PrincipalVariation);
        Assert.Equal(3, request.MoveNumber);
    }

    // ─── 測試 3: MoveCompletedEventArgs 建構正確 ───────────────────────

    [Fact]
    public void MoveCompletedEventArgs_ShouldBuildWithAllFields()
    {
        var args = new MoveCompletedEventArgs
        {
            Fen = TestFen,
            MoveNotation = "炮二平五",
            Score = 50,
            Depth = 10,
            Nodes = 1000000,
            PvLine = "炮二平五 馬8進7",
            MovedBy = PieceColor.Black,
            MoveNumber = 5
        };

        Assert.Equal(TestFen, args.Fen);
        Assert.Equal("炮二平五", args.MoveNotation);
        Assert.Equal(50, args.Score);
        Assert.Equal(10, args.Depth);
        Assert.Equal(1000000, args.Nodes);
        Assert.Equal("炮二平五 馬8進7", args.PvLine);
        Assert.Equal(PieceColor.Black, args.MovedBy);
        Assert.Equal(5, args.MoveNumber);
    }

    // ─── 測試 4: AnalyzeAsync 呼叫正確端點並帶有 system prompt ──────────

    [Fact]
    public async Task AnalyzeAsync_ShouldCallChatCompletionsWithSystemPrompt()
    {
        var capturedRequests = new List<string>();
        var settings = new GameAnalysisSettings
        {
            Endpoint = "https://test.local/v1",
            Model = "test-model",
            ApiKey = "test-key",
            SystemPrompt = "你是象棋分析師。",
            Temperature = 0,
            MaxTokens = 512,
            TimeoutSeconds = 5
        };

        using var httpClient = new HttpClient(new CapturingHandler(async req =>
        {
            capturedRequests.Add(await req.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { message = new { content = "局面有利於紅方" } } }
                    }),
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var service = new GameAnalysisService(settings, httpClient);
        var request = new GameAnalysisRequest
        {
            Fen = TestFen,
            MovedBy = PieceColor.Red,
            LastMoveNotation = "炮二平五",
            Score = 50,
            SearchDepth = 6,
            Nodes = 200000,
            MoveNumber = 1
        };

        var result = await service.AnalyzeAsync(request);

        Assert.Equal("局面有利於紅方", result);
        Assert.Single(capturedRequests);

        using var doc = JsonDocument.Parse(capturedRequests[0]);
        // 確認 model 欄位正確
        Assert.Equal("test-model", doc.RootElement.GetProperty("model").GetString());
        // 確認 system prompt 送出
        Assert.Equal("你是象棋分析師。", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        // 確認 user message 包含 FEN
        var userMessage = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.NotNull(userMessage);
        Assert.Contains(TestFen, userMessage);
        // 確認 user message 包含走法記號
        Assert.Contains("炮二平五", userMessage);
    }

    // ─── 測試 5: AnalyzeAsync 支援 streaming（IProgress<string>）──────────

    [Fact]
    public async Task AnalyzeAsync_ShouldReportProgress_WhenProgressProvided()
    {
        var progressItems = new List<string>();
        var settings = new GameAnalysisSettings
        {
            Endpoint = "https://test.local/v1",
            Model = "test-model",
            SystemPrompt = "你是分析師。",
            MaxTokens = 256
        };

        using var httpClient = new HttpClient(new CapturingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    string.Join("\n",
                        "data: {\"choices\":[{\"delta\":{\"content\":\"局面\"}}]}",
                        "data: {\"choices\":[{\"delta\":{\"content\":\"均衡\"}}]}",
                        "data: [DONE]"),
                    Encoding.UTF8,
                    "application/json")
            })
        ));

        var service = new GameAnalysisService(settings, httpClient);
        var request = new GameAnalysisRequest { Fen = TestFen, LastMoveNotation = "炮二平五", MoveNumber = 1 };

        var progress = new Progress<string>(text => progressItems.Add(text));
        var result = await service.AnalyzeAsync(request, progress);

        Assert.Equal("局面均衡", result);
        Assert.NotEmpty(progressItems);
    }

    // ─── 測試 6: AnalyzeAsync 取消時應正確結束 ───────────────────────────

    [Fact]
    public async Task AnalyzeAsync_ShouldThrowOperationCanceledException_WhenCanceled()
    {
        var settings = new GameAnalysisSettings
        {
            Endpoint = "https://test.local/v1",
            Model = "test-model",
            SystemPrompt = "你是分析師。",
            MaxTokens = 256
        };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using var httpClient = new HttpClient(new CapturingHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var service = new GameAnalysisService(settings, httpClient);
        var request = new GameAnalysisRequest { Fen = TestFen, LastMoveNotation = "炮二平五", MoveNumber = 1 };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AnalyzeAsync(request, ct: cts.Token));
    }

    // ─── Helper ────────────────────────────────────────────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

        public CapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            this.handler = (request, _) => handler(request);
        }

        public CapturingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
