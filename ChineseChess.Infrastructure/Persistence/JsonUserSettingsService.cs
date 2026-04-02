using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChineseChess.Infrastructure.Persistence;

/// <summary>
/// 將使用者引擎設定以 JSON 格式持久化至 engine-user-settings.json。
/// Load 失敗（檔案不存在或損壞）→ 靜默回傳預設值。
/// Save 失敗 → 記錄 Trace 警告，不拋例外。
/// </summary>
public class JsonUserSettingsService : IUserSettingsService
{
    private static readonly string SettingsFilePath =
        Path.Combine(AppContext.BaseDirectory, "engine-user-settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = PersistenceJsonOptions.Default;

    public ExternalEngineSettings LoadEngineSettings()
    {
        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<ExternalEngineSettings>(json, SerializerOptions)
                   ?? new ExternalEngineSettings();
        }
        catch (FileNotFoundException)
        {
            return new ExternalEngineSettings();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"載入引擎設定失敗，使用預設值：{ex.GetType().Name}");
            return new ExternalEngineSettings();
        }
    }

    public void SaveEngineSettings(ExternalEngineSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"儲存引擎設定失敗：{ex.GetType().Name}");
        }
    }
}
