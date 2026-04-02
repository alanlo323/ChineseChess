using ChineseChess.Application.Enums;

namespace ChineseChess.Application.Configuration;

/// <summary>
/// 已成功載入的外部引擎資訊（不可變 record）。
/// 儲存於 loaded-engines.json，下次啟動時自動恢復。
/// </summary>
public record LoadedEngineInfo
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ExecutablePath { get; init; } = string.Empty;
    public string EngineName { get; init; } = string.Empty;
    public string EngineAuthor { get; init; } = string.Empty;
    public EngineProtocol Protocol { get; init; }
    public int? EloRating { get; init; }
    public DateTime DiscoveredAt { get; init; } = DateTime.Now;

    /// <summary>顯示名稱：有 ELO 時附加 "(ELO xxxx)"。</summary>
    public string DisplayName => EloRating.HasValue
        ? $"{EngineName} (ELO {EloRating})"
        : EngineName;
}
