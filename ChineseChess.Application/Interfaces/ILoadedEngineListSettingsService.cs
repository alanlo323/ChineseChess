using ChineseChess.Application.Configuration;

namespace ChineseChess.Application.Interfaces;

/// <summary>
/// 已載入引擎列表的持久化服務（讀寫 loaded-engines.json）。
/// </summary>
public interface ILoadedEngineListSettingsService
{
    LoadedEngineListSettings LoadSettings();
    void SaveSettings(LoadedEngineListSettings settings);
}
