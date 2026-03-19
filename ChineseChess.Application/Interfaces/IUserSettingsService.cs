using ChineseChess.Application.Configuration;

namespace ChineseChess.Application.Interfaces;

/// <summary>
/// 使用者偏好設定的讀寫介面（外部引擎路徑、協議等持久化設定）。
/// </summary>
public interface IUserSettingsService
{
    ExternalEngineSettings LoadEngineSettings();
    void SaveEngineSettings(ExternalEngineSettings settings);
}
