using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using System.Diagnostics;
using System.Text.Json;

namespace ChineseChess.Infrastructure.Persistence;

/// <summary>
/// 將已載入 NNUE 模型列表以 JSON 格式持久化至 loaded-nnue-models.json。
/// Load 失敗 → 靜默回傳空列表；Save 失敗 → 記錄 Trace 警告，不拋例外。
/// </summary>
public class JsonLoadedNnueModelListSettingsService : ILoadedNnueModelListSettingsService
{
    private static readonly string SettingsFilePath =
        Path.Combine(AppContext.BaseDirectory, "loaded-nnue-models.json");

    private static readonly JsonSerializerOptions SerializerOptions = PersistenceJsonOptions.Default;

    public LoadedNnueModelListSettings LoadSettings()
    {
        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<LoadedNnueModelListSettings>(json, SerializerOptions)
                   ?? new LoadedNnueModelListSettings();
        }
        catch (FileNotFoundException)
        {
            return new LoadedNnueModelListSettings();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"載入 NNUE 模型列表設定失敗，使用空列表：{ex.GetType().Name}");
            return new LoadedNnueModelListSettings();
        }
    }

    public void SaveSettings(LoadedNnueModelListSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"儲存 NNUE 模型列表設定失敗：{ex.GetType().Name}");
        }
    }
}
