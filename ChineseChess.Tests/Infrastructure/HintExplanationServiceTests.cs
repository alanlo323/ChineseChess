using ChineseChess.Application.Configuration;
using ChineseChess.Application.Models;
using ChineseChess.Infrastructure.AI.Hint;
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

public class HintExplanationServiceTests
{
    [Fact]
    public async Task ExplainAsync_ShouldCallChatCompletionsAndReturnContent()
    {
        var captured = new List<HttpRequestMessage>();
        var settings = new HintExplanationSettings
        {
            Endpoint = "https://test.local/v1",
            Model = "test-model",
            ApiKey = "test-key",
            SystemPrompt = "你是自訂象棋專家，務必精簡回應。",
            Temperature = 0.6,
            MaxTokens = 512,
            TimeoutSeconds = 5
        };

        using var httpClient = new HttpClient(new CapturingHandler(async req =>
        {
            captured.Add(req);
            var body = await req.Content!.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("test-model", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal("你是自訂象棋專家，務必精簡回應。", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal(0.6, doc.RootElement.GetProperty("temperature").GetDouble());
            Assert.Equal(512, doc.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Contains(req.Headers.Accept, media => media.MediaType == "application/json");
            Assert.Contains("思路樹", doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.Contains("【推理模式】", doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.NotNull(req.Headers.Authorization);
            Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", req.Headers.Authorization!.Parameter);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        choices = new[]
                        {
                            new { message = new { content = "這步保證主動出擊空間，限制對方還擊。" } }
                        }
                    }),
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var service = new OpenAICompatibleHintExplanationService(settings, httpClient);
        var request = new HintExplanationRequest
        {
            Fen = "4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1",
            BestMoveNotation = "車一進一",
            Score = 12,
            SearchDepth = 3,
            Nodes = 1234,
            PrincipalVariation = "車一進一 馬八進七",
            ThinkingTree = "【思路樹】\n1. 先手\n  ...\n"
        };

        var explanation = await service.ExplainAsync(request);

        Assert.Single(captured);
        Assert.Equal("https://test.local/v1/chat/completions", captured[0].RequestUri!.ToString());
        Assert.Equal("這步保證主動出擊空間，限制對方還擊。", explanation);
    }

    [Fact]
    public async Task ExplainAsync_ShouldSkipReasoningPrompt_WhenDisabled()
    {
        var capturedBodies = new List<string>();
        var settings = new HintExplanationSettings
        {
            Endpoint = "https://test.local/v1",
            Model = "test-model",
            EnableReasoning = false
        };

        using var httpClient = new HttpClient(new CapturingHandler(async req =>
        {
            capturedBodies.Add(await req.Content!.ReadAsStringAsync());

            return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { message = new { content = "回應" } } }
                    }),
                    Encoding.UTF8,
                    "application/json")
            });
        }));

        var service = new OpenAICompatibleHintExplanationService(settings, httpClient);
        var request = new HintExplanationRequest { Fen = "4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1", BestMoveNotation = "車一進一" };

        await service.ExplainAsync(request);

        Assert.Single(capturedBodies);
        using var doc = JsonDocument.Parse(capturedBodies[0]);
        Assert.DoesNotContain("【推理模式】", doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ExplainAsync_ShouldAcceptEndpointWithChatCompletionsPath()
    {
        var settings = new HintExplanationSettings
        {
            Endpoint = "https://test.local/v1/chat/completions",
            Model = "test-model"
        };

        using var httpClient = new HttpClient(new CapturingHandler(_ =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        choices = new[]
                        {
                            new { message = new { content = "回應" } }
                        }
                    }),
                    Encoding.UTF8,
                    "application/json")
            });
        }));

        var service = new OpenAICompatibleHintExplanationService(settings, httpClient);
        var request = new HintExplanationRequest { Fen = "4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1", BestMoveNotation = "車一進一" };

        var explanation = await service.ExplainAsync(request);

        Assert.Equal("回應", explanation);
    }

    [Fact]
    public async Task ExplainAsync_ShouldReportProgress_WhenStreamingEnabled()
    {
        var settings = new HintExplanationSettings
        {
            Endpoint = "https://test.local/v1",
            Model = "test-model",
            SystemPrompt = "你是象棋老師",
            MaxTokens = 256
        };

        var progressResults = new List<string>();
        var capturedRequestBodies = new List<string>();
        using var httpClient = new HttpClient(new CapturingHandler(async req =>
        {
            capturedRequestBodies.Add(await req.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    string.Join(
                        "\n",
                        new[]
                        {
                            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"回應\"}}]}",
                            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"內容\"}}],\"usage\":{\"completion_tokens\":112,\"total_tokens\":160}}",
                            "data: [DONE]"
                        }),
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var service = new OpenAICompatibleHintExplanationService(settings, httpClient);
        var request = new HintExplanationRequest
        {
            Fen = "4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1",
            BestMoveNotation = "車一進一"
        };

        var explanation = await service.ExplainAsync(
            request,
            new Progress<string>(text => progressResults.Add(text)),
            CancellationToken.None);

        Assert.Single(capturedRequestBodies);
        using var requestDocument = JsonDocument.Parse(capturedRequestBodies[0]);
        Assert.True(requestDocument.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("回應內容", explanation);
        Assert.NotEmpty(progressResults);
        Assert.Contains("回應內容", progressResults.Last());
        Assert.Contains("（已輸出Token：112）", progressResults.Last());
    }

    [Fact]
    public async Task ExplainAsync_ShouldIgnoreUsageToken_WhenParsingRegularResponse()
    {
        var settings = new HintExplanationSettings
        {
            Endpoint = "https://test.local/v1",
            Model = "test-model",
            SystemPrompt = "你是象棋老師",
            MaxTokens = 256
        };

        using var httpClient = new HttpClient(new CapturingHandler(_ =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        choices = new[]
                        {
                            new { message = new { content = "回應內容" } }
                        },
                        usage = new
                        {
                            prompt_tokens = 48,
                            completion_tokens = 112,
                            total_tokens = 160
                        }
                    }),
                    Encoding.UTF8,
                    "application/json")
            });
        }));

        var service = new OpenAICompatibleHintExplanationService(settings, httpClient);
        var request = new HintExplanationRequest
        {
            Fen = "4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1",
            BestMoveNotation = "車一進一"
        };

        var explanation = await service.ExplainAsync(request);

        Assert.Equal("回應內容", explanation);
    }

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
        {
            return handler(request, cancellationToken);
        }
    }
}
