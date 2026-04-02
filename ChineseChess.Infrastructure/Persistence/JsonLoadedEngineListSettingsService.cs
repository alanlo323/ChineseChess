using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using System.Diagnostics;
using System.Text.Json;

namespace ChineseChess.Infrastructure.Persistence;

/// <summary>
/// 將已載入引擎列表以 JSON 格式持久化至 loaded-engines.json。
/// Load 失敗（檔案不存在或損壞）→ 靜默回傳空列表。
/// Save 失敗 → 記錄 Trace 警告，不拋例外。
/// </summary>
public class JsonLoadedEngineListSettingsService : ILoadedEngineListSettingsService
{
    private static readonly string SettingsFilePath =
        Path.Combine(AppContext.BaseDirectory, "loaded-engines.json");

    private static readonly JsonSerializerOptions SerializerOptions = PersistenceJsonOptions.Default;

    public LoadedEngineListSettings LoadSettings()
    {
        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<LoadedEngineListSettings>(json, SerializerOptions)
                   ?? new LoadedEngineListSettings();
        }
        catch (FileNotFoundException)
        {
            return new LoadedEngineListSettings();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"載入引擎列表設定失敗，使用空列表：{ex.GetType().Name}");
            return new LoadedEngineListSettings();
        }
    }

    public void SaveSettings(LoadedEngineListSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"儲存引擎列表設定失敗：{ex.GetType().Name}");
        }
    }
}
