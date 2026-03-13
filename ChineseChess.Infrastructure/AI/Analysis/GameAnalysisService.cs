using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Hint;
using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Infrastructure.AI.Analysis;

/// <summary>
/// 以 OpenAI Compatible API 實作局面分析服務。
/// 內部委派給 <see cref="OpenAICompatibleHintExplanationService"/>，
/// 並將 <see cref="GameAnalysisRequest"/> 轉換為對應的 <see cref="HintExplanationRequest"/>。
/// </summary>
public sealed class GameAnalysisService : IGameAnalysisService, IDisposable
{
    private readonly OpenAICompatibleHintExplanationService delegateService;

    public GameAnalysisService(GameAnalysisSettings settings)
    {
        var hintSettings = MapToHintSettings(settings);
        delegateService = new OpenAICompatibleHintExplanationService(hintSettings);
    }

    /// <summary>供測試注入 mock HttpClient。</summary>
    public GameAnalysisService(GameAnalysisSettings settings, HttpClient httpClient)
    {
        var hintSettings = MapToHintSettings(settings);
        delegateService = new OpenAICompatibleHintExplanationService(hintSettings, httpClient);
    }

    public void Dispose() => delegateService.Dispose();

    public Task<string> AnalyzeAsync(GameAnalysisRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var hintRequest = new HintExplanationRequest
        {
            Fen = request.Fen,
            SideToMove = request.MovedBy,
            BestMoveNotation = request.LastMoveNotation,
            Score = request.Score,
            SearchDepth = request.SearchDepth,
            Nodes = request.Nodes,
            PrincipalVariation = request.PrincipalVariation,
            ThinkingTree = FormatMoveContext(request)
        };

        return delegateService.ExplainAsync(hintRequest, progress, ct);
    }

    private static string FormatMoveContext(GameAnalysisRequest request)
    {
        var color = request.MovedBy == PieceColor.Red ? "紅方" : "黑方";
        return string.Format(
            CultureInfo.InvariantCulture,
            "走棋方：{0}，第 {1} 步",
            color,
            request.MoveNumber);
    }

    private static HintExplanationSettings MapToHintSettings(GameAnalysisSettings settings) =>
        new HintExplanationSettings
        {
            Endpoint = settings.Endpoint,
            Model = settings.Model,
            ApiKey = settings.ApiKey,
            SystemPrompt = settings.SystemPrompt,
            Temperature = settings.Temperature,
            MaxTokens = settings.MaxTokens,
            TimeoutSeconds = settings.TimeoutSeconds,
            EnableReasoning = settings.EnableReasoning,
            ReasoningEffort = settings.ReasoningEffort
        };
}
