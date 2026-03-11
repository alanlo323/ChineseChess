using ChineseChess.Application.Models;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Interfaces;

public interface IHintExplanationService
{
    Task<string> ExplainAsync(HintExplanationRequest request, IProgress<string>? progress = null, CancellationToken ct = default);
}

