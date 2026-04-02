using ChineseChess.Application.Configuration;

namespace ChineseChess.Application.Interfaces;

/// <summary>
/// 已載入 NNUE 模型列表的登錄介面（Application 層，不暴露 INnueNetwork）。
/// </summary>
public interface ILoadedNnueModelRegistry
{
    IReadOnlyList<LoadedNnueModelInfo> Models { get; }
    event Action? ModelsChanged;

    Task<LoadedNnueModelInfo> AddModelAsync(string filePath, CancellationToken ct = default);
    void RemoveModel(string modelId);
    LoadedNnueModelInfo? GetModelInfo(string modelId);
    bool IsModelLoaded(string modelId);
}
