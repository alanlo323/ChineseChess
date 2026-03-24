using ChineseChess.Application.Configuration;

namespace ChineseChess.Application.Interfaces;

/// <summary>
/// NNUE 設定的讀寫介面。
/// </summary>
public interface INnueSettingsService
{
    NnueSettings LoadNnueSettings();
    void SaveNnueSettings(NnueSettings settings);
}
