using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using Microsoft.ML.Tokenizers;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Infrastructure.AI.Hint;

public sealed class OpenAICompatibleHintExplanationService : IHintExplanationService, IDisposable
{
    private readonly HintExplanationSettings settings;
    private readonly HttpClient httpClient;
    // 僅在無參建構子自行建立 HttpClient 時才負責 Dispose（外部注入者自行管理）
    private readonly bool ownsHttpClient;
    private static readonly ConcurrentDictionary<string, TiktokenTokenizer> TokenizerCache = new(StringComparer.OrdinalIgnoreCase);
    private const string DefaultEncoding = "cl100k_base";

    private const string ChatCompletionsPath = "/chat/completions";

    public OpenAICompatibleHintExplanationService(HintExplanationSettings settings)
        : this(settings, new HttpClient { Timeout = GetTimeout(settings.TimeoutSeconds) }, ownsHttpClient: true)
    {
    }

    public OpenAICompatibleHintExplanationService(HintExplanationSettings settings, HttpClient httpClient)
        : this(settings, httpClient, ownsHttpClient: false)
    {
    }

    private OpenAICompatibleHintExplanationService(HintExplanationSettings settings, HttpClient httpClient, bool ownsHttpClient)
    {
        this.settings = settings;
        this.httpClient = httpClient;
        this.ownsHttpClient = ownsHttpClient;
    }

    public void Dispose()
    {
        if (ownsHttpClient) httpClient.Dispose();
    }

    public async Task<string> ExplainAsync(HintExplanationRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            throw new InvalidOperationException("Hint explanation model is missing.");
        }
        if (string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            throw new InvalidOperationException("Hint explanation system prompt is missing.");
        }
        if (settings.Temperature is < 0 or > 2)
        {
            throw new InvalidOperationException("Hint explanation temperature must be between 0 and 2.");
        }
        if (settings.MaxTokens <= 0)
        {
            throw new InvalidOperationException("Hint explanation max tokens must be greater than 0.");
        }

        var endpoint = ResolveEndpoint();
        var prompt = BuildPrompt(request);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint);
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        var reasoning = settings.EnableReasoning
            ? new ReasoningConfig(settings.ReasoningEffort)
            : null;

        var payload = new ChatCompletionRequest(
            settings.Model,
            [
                new ChatMessage("system", settings.SystemPrompt),
                new ChatMessage("user", prompt)
            ],
            Temperature: settings.Temperature,
            MaxTokens: settings.MaxTokens,
            Stream: progress is not null,
            Reasoning: reasoning);

        requestMessage.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            response.EnsureSuccessStatusCode();
            return raw;
        }

        if (progress is null)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            return ParseResponse(raw);
        }

        var responseBuilder = new StringBuilder();
        var rawPayload = new StringBuilder();
        long estimatedCompletionTokens = 0;
        long estimatedReasoningTokens = 0;
        await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var streamReader = new StreamReader(responseStream);
        while (true)
        {
            var line = await streamReader.ReadLineAsync();
            if (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (line is null)
            {
                break;
            }
            rawPayload.AppendLine(line);

            var trimmedLine = line.AsSpan().Trim().ToString();
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }

            if (!trimmedLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (TryExtractMessageContent(trimmedLine, out var fallbackContent))
                {
                    responseBuilder.Append(fallbackContent);
                    estimatedCompletionTokens += EstimateTokenCount(fallbackContent);
                    progress.Report(FormatProgressText(
                        responseBuilder.ToString(),
                        new TokenUsage(estimatedCompletionTokens, estimatedCompletionTokens, null)));
                }

                continue;
            }

            var payloadText = trimmedLine.AsSpan("data:".Length).ToString().Trim();
            if (string.Equals(payloadText, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(payloadText))
            {
                continue;
            }

            var hasUsage = TryExtractTokenUsage(payloadText, out var tokenUsage);
            var delta = ExtractDeltaContent(payloadText);
            var reasoningContent = ExtractReasoningContent(payloadText);
            if (!string.IsNullOrEmpty(delta))
            {
                responseBuilder.Append(delta);
            }
            else if (!hasUsage && string.IsNullOrWhiteSpace(reasoningContent))
            {
                continue;
            }

            if (!hasUsage && !string.IsNullOrEmpty(delta))
            {
                estimatedCompletionTokens += EstimateTokenCount(delta);
            }

            if (!hasUsage && !string.IsNullOrWhiteSpace(reasoningContent))
            {
                estimatedReasoningTokens += EstimateTokenCount(reasoningContent);
            }

            if (hasUsage || !string.IsNullOrEmpty(delta) || !string.IsNullOrWhiteSpace(reasoningContent))
            {
                long? currentReasoningTokens = estimatedReasoningTokens > 0 ? estimatedReasoningTokens : null;
                progress.Report(FormatProgressText(
                    responseBuilder.ToString(),
                    hasUsage ? tokenUsage : new TokenUsage(estimatedCompletionTokens + estimatedReasoningTokens, estimatedCompletionTokens, currentReasoningTokens)));
            }
        }

        response.EnsureSuccessStatusCode();
        if (responseBuilder.Length == 0)
        {
            var finalResponse = ParseResponse(rawPayload.ToString());
            progress.Report(FormatProgressText(
                finalResponse,
                new TokenUsage(EstimateTokenCount(finalResponse), null, null)));
            return finalResponse;
        }

        return responseBuilder.ToString().Trim();

    }

    private static string? ExtractDeltaContent(string streamChunk)
    {
        using var document = JsonDocument.Parse(streamChunk);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return null;
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("delta", out var deltaElement))
        {
            return null;
        }

        if (!deltaElement.TryGetProperty("content", out var contentElement))
        {
            return null;
        }

        return contentElement.GetString();
    }

    private static bool TryExtractMessageContent(string streamChunk, out string? content)
    {
        content = null;
        try
        {
            using var document = JsonDocument.Parse(streamChunk);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return false;
            }

            var firstChoice = choices[0];
            if (!firstChoice.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var contentElement))
            {
                return false;
            }

            content = contentElement.GetString();
            return !string.IsNullOrWhiteSpace(content);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractReasoningContent(string streamChunk)
    {
        try
        {
            using var document = JsonDocument.Parse(streamChunk);
            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return null;
            }

            var firstChoice = choices[0];
            if (!firstChoice.TryGetProperty("delta", out var deltaElement) ||
                !deltaElement.TryGetProperty("reasoning_content", out var reasoningElement))
            {
                return null;
            }

            return reasoningElement.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryExtractTokenUsage(string streamChunk, out TokenUsage usage)
    {
        usage = default;
        using var document = JsonDocument.Parse(streamChunk);
        if (!document.RootElement.TryGetProperty("usage", out var usageElement))
        {
            return false;
        }

        var hasValue = false;
        long? totalTokens = null;
        long? completionTokens = null;
        long? reasoningTokens = null;

        if (usageElement.TryGetProperty("total_tokens", out var totalTokensElement) &&
            TryGetInt64(totalTokensElement, out var totalTokensValue))
        {
            totalTokens = totalTokensValue;
            hasValue = true;
        }

        if (usageElement.TryGetProperty("completion_tokens", out var completionTokensElement) &&
            TryGetInt64(completionTokensElement, out var completionTokensValue))
        {
            completionTokens = completionTokensValue;
            hasValue = true;
        }

        if (usageElement.TryGetProperty("completion_tokens_details", out var completionDetails) &&
            completionDetails.ValueKind == JsonValueKind.Object &&
            completionDetails.TryGetProperty("reasoning_tokens", out var reasoningTokenElement) &&
            TryGetInt64(reasoningTokenElement, out var reasoningTokensFromCompletion))
        {
            reasoningTokens = reasoningTokensFromCompletion;
            hasValue = true;
        }

        if (usageElement.TryGetProperty("total_tokens_details", out var totalDetails) &&
            totalDetails.ValueKind == JsonValueKind.Object &&
            totalDetails.TryGetProperty("reasoning_tokens", out var totalReasoningTokenElement) &&
            TryGetInt64(totalReasoningTokenElement, out var reasoningTokensFromTotal))
        {
            reasoningTokens = reasoningTokensFromTotal;
            hasValue = true;
        }

        if (!hasValue)
        {
            return false;
        }

        usage = new TokenUsage(totalTokens, completionTokens, reasoningTokens);
        return true;
    }

    private static bool TryGetInt64(JsonElement element, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return element.TryGetInt64(out value);
    }

    private long EstimateTokenCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var encoding = ResolveEncodingForModel();
        TiktokenTokenizer tokenizer;
        try
        {
            tokenizer = TokenizerCache.GetOrAdd(encoding, CreateTokenizerForEncoding);
        }
        catch
        {
            return EstimateTokenCountFallback(text);
        }

        try
        {
            return tokenizer.CountTokens(text);
        }
        catch
        {
            var bytes = Encoding.UTF8.GetByteCount(text);
            return (long)Math.Ceiling(bytes / 4.0);
        }
    }

    private static TiktokenTokenizer CreateTokenizerForEncoding(string encoding)
    {
        return TiktokenTokenizer.CreateForEncoding(encoding);
    }

    private string ResolveEncodingForModel()
    {
        var model = settings.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return DefaultEncoding;
        }

        if (ModelEncodingMap.TryGetValue(model, out var mappedEncoding))
        {
            return mappedEncoding;
        }

        return DefaultEncoding;
    }

    private static readonly Dictionary<string, string> ModelEncodingMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4"] = "cl100k_base",
        ["gpt-4o"] = "cl100k_base",
        ["gpt-4-turbo"] = "cl100k_base",
        ["gpt-4o-mini"] = "cl100k_base",
        ["gpt-4-0125-preview"] = "cl100k_base",
        ["gpt-3.5-turbo"] = "cl100k_base",
        ["gpt-3.5-turbo-16k"] = "cl100k_base"
    };

    private static long EstimateTokenCountFallback(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        return (long)Math.Ceiling(bytes / 4.0);
    }

    private static string FormatProgressText(string text, TokenUsage? usage)
    {
        if (!usage.HasValue)
        {
            return text;
        }

        var currentUsage = usage.Value;
        var totalTokens = currentUsage.TotalTokens ?? currentUsage.CompletionTokens;
        if (!totalTokens.HasValue && !currentUsage.ReasoningTokens.HasValue)
        {
            return text;
        }

        var status = $"（已輸出總Token：{(totalTokens.HasValue ? totalTokens.Value.ToString() : "-")}";
        if (currentUsage.ReasoningTokens.HasValue)
        {
            status += $"，推理：{currentUsage.ReasoningTokens.Value}";
        }

        return $"{text}{status}）";
    }

    private readonly record struct TokenUsage(long? TotalTokens, long? CompletionTokens, long? ReasoningTokens);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private string ResolveEndpoint()
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            throw new InvalidOperationException("Hint explanation endpoint is missing.");
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Hint explanation endpoint is invalid.");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var alreadyCompletion = path.EndsWith(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase);
        if (alreadyCompletion)
        {
            return uri.ToString();
        }

        return $"{uri}{ChatCompletionsPath}";
    }

    private static TimeSpan GetTimeout(int seconds) =>
        TimeSpan.FromSeconds(Math.Max(1, seconds));

    private string BuildPrompt(HintExplanationRequest request)
    {
        var prompt = string.Format(
            CultureInfo.InvariantCulture,
            "請根據下列中國象棋局面資料，回答建議走法解釋：\n" +
            "FEN: {0}\n" +
            "走棋方: {1}\n" +
            "建議走法: {2}\n" +
            "AI 評分: {3}\n" +
            "搜尋深度: {4}\n" +
            "節點數: {5}\n" +
            "PV: {6}\n" +
            "思路樹: {7}",
            request.Fen,
            request.SideToMove,
            request.BestMoveNotation,
            request.Score,
            request.SearchDepth,
            request.Nodes,
            string.IsNullOrWhiteSpace(request.PrincipalVariation) ? "(未提供)" : request.PrincipalVariation,
            string.IsNullOrWhiteSpace(request.ThinkingTree) ? "(未提供)" : request.ThinkingTree);

        return prompt;
    }

    private static string ParseResponse(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Empty response from hint explanation service.");
        }

        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("No answer choices returned.");
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException("Invalid answer format from hint explanation service.");
        }

        var result = content.GetString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException("Hint explanation response was empty.");
        }

        return result.Trim();
    }

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ReasoningConfig(
        [property: JsonPropertyName("effort")] string Effort = "high");

    private sealed record ChatCompletionRequest(
        string Model,
        IReadOnlyList<ChatMessage> Messages,
        double Temperature = 0.2,
        [property: JsonPropertyName("max_tokens")] int MaxTokens = 1200,
        [property: JsonPropertyName("stream")] bool Stream = false,
        [property: JsonPropertyName("reasoning"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReasoningConfig? Reasoning = null);
}

