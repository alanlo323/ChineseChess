using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Services;

/// <summary>棋局記錄序列化服務（JSON / .ccgame 格式）。</summary>
public class GameRecordService : IGameRecordService
{
    private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Serialize(GameRecord record)
    {
        return JsonSerializer.Serialize(record, WriteOptions);
    }

    public GameRecord Deserialize(string json)
    {
        var record = JsonSerializer.Deserialize<GameRecord>(json, ReadOptions)
            ?? throw new JsonException("反序列化結果為 null");

        if (record.FormatVersion != 1)
            throw new JsonException($"不支援的棋局格式版本：{record.FormatVersion}");

        return record;
    }

    public async Task ExportAsync(GameRecord record, string filePath, CancellationToken ct = default)
    {
        var json = Serialize(record);
        await File.WriteAllTextAsync(filePath, json, System.Text.Encoding.UTF8, ct);
    }

    public async Task<GameRecord> ImportAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"棋局檔案不存在：{filePath}");

        var json = await File.ReadAllTextAsync(filePath, System.Text.Encoding.UTF8, ct);
        return Deserialize(json);
    }
}
