using ChineseChess.Application.Configuration;

namespace ChineseChess.Application.Interfaces;

/// <summary>NNUE 模型列表持久化服務介面。</summary>
public interface ILoadedNnueModelListSettingsService
{
    LoadedNnueModelListSettings LoadSettings();
    void SaveSettings(LoadedNnueModelListSettings settings);
}
