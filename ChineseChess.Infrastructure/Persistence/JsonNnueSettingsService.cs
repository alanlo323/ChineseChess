using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChineseChess.Infrastructure.Persistence;

/// <summary>
/// 將 NNUE 設定以 JSON 格式持久化至 nnue-user-settings.json。
/// Load 失敗（檔案不存在或損壞）→ 靜默回傳預設值。
/// Save 失敗 → 記錄 Trace 警告，不拋例外。
/// </summary>
public class JsonNnueSettingsService : INnueSettingsService
{
    private static readonly string SettingsFilePath =
        Path.Combine(AppContext.BaseDirectory, "nnue-user-settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public NnueSettings LoadNnueSettings()
    {
        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<NnueSettings>(json, SerializerOptions)
                   ?? new NnueSettings();
        }
        catch (FileNotFoundException)
        {
            return new NnueSettings();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"載入 NNUE 設定失敗，使用預設值：{ex.GetType().Name}");
            return new NnueSettings();
        }
    }

    public void SaveNnueSettings(NnueSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"儲存 NNUE 設定失敗：{ex.GetType().Name}");
        }
    }
}
