using ChineseChess.Application.Configuration;

namespace ChineseChess.Application.Interfaces;

/// <summary>
/// 已載入引擎的登錄表：管理引擎的生命週期、自動偵測協議、持久化。
/// </summary>
public interface ILoadedEngineRegistry : IDisposable
{
    /// <summary>目前登錄的所有引擎資訊（唯讀快照）。</summary>
    IReadOnlyList<LoadedEngineInfo> Engines { get; }

    /// <summary>引擎列表有異動時觸發（新增、重載、移除）。</summary>
    event Action? EnginesChanged;

    /// <summary>
    /// 載入引擎：自動偵測協議（UCCI 優先），取得 id name / author / ELO，
    /// 加入列表並持久化。
    /// </summary>
    Task<LoadedEngineInfo> AddEngineAsync(string executablePath, CancellationToken ct = default);

    /// <summary>重新啟動引擎 process，更新 EngineName / ELO。</summary>
    Task ReloadEngineAsync(string engineId, CancellationToken ct = default);

    /// <summary>卸載引擎：結束 process，從列表移除，更新持久化。</summary>
    void RemoveEngine(string engineId);

    /// <summary>依 ID 取得引擎靜態資訊。</summary>
    LoadedEngineInfo? GetEngineInfo(string engineId);

    /// <summary>取得已連線的 Adapter（供 AiPlayerSettingsViewModel 使用）。</summary>
    IExternalEngineAdapter? GetActiveAdapter(string engineId);
}
