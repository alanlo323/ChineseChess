using ChineseChess.Application.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Interfaces;

public interface IGameAnalysisService
{
    Task<string> AnalyzeAsync(GameAnalysisRequest request, IProgress<string>? progress = null, CancellationToken ct = default);
}
