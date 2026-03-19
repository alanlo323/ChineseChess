using ChineseChess.Application.Enums;
using ChineseChess.Application.Models;
using ChineseChess.Application.Services;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChineseChess.Tests.Application;

public class GameRecordSerializationTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    private static GameRecord BuildSampleRecord() => new GameRecord
    {
        FormatVersion = 1,
        Metadata = new GameRecordMetadata
        {
            RedPlayer   = "玩家",
            BlackPlayer = "AI（深度 8）",
            Date        = "2026-03-18 14:30",
            Result      = "紅勝",
            GameMode    = GameMode.PlayerVsAi,
        },
        InitialFen = InitialFen,
        Steps = new List<GameRecordStep>
        {
            new GameRecordStep { StepNumber = 1, From = 67, To = 40, Notation = "炮二平五", Turn = "Red",   IsCapture = false },
            new GameRecordStep { StepNumber = 2, From = 19, To = 38, Notation = "馬8進7",   Turn = "Black", IsCapture = false },
        },
    };

    [Fact]
    public void Serialize_ThenDeserialize_ShouldRoundTrip()
    {
        var svc = new GameRecordService();
        var original = BuildSampleRecord();

        var json = svc.Serialize(original);
        var restored = svc.Deserialize(json);

        Assert.Equal(original.FormatVersion, restored.FormatVersion);
        Assert.Equal(original.InitialFen, restored.InitialFen);
        Assert.Equal(original.Metadata.RedPlayer, restored.Metadata.RedPlayer);
        Assert.Equal(original.Metadata.BlackPlayer, restored.Metadata.BlackPlayer);
        Assert.Equal(original.Metadata.GameMode, restored.Metadata.GameMode);
        Assert.Equal(original.Steps.Count, restored.Steps.Count);
        Assert.Equal(original.Steps[0].Notation, restored.Steps[0].Notation);
        Assert.Equal(original.Steps[1].Turn, restored.Steps[1].Turn);
    }

    [Fact]
    public void Serialize_ShouldProduceValidJson()
    {
        var svc = new GameRecordService();
        var record = BuildSampleRecord();

        var json = svc.Serialize(record);

        // 驗證可被系統 JSON 解析器接受（不拋例外即為合法）
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("formatVersion").GetInt32());
    }

    [Fact]
    public void Deserialize_WithUnsupportedVersion_ShouldThrow()
    {
        var svc = new GameRecordService();
        var record = BuildSampleRecord() with { FormatVersion = 99 };
        var json = svc.Serialize(record);
        // 強制版本為 99
        json = json.Replace("\"formatVersion\": 1", "\"formatVersion\": 99");

        Assert.Throws<JsonException>(() => svc.Deserialize(json));
    }

    [Fact]
    public void Deserialize_WithInvalidJson_ShouldThrow()
    {
        var svc = new GameRecordService();
        Assert.Throws<JsonException>(() => svc.Deserialize("{ invalid }"));
    }

    [Fact]
    public async Task ExportAsync_ThenImportAsync_ShouldRoundTrip()
    {
        var svc = new GameRecordService();
        var original = BuildSampleRecord();
        var tempFile = Path.GetTempFileName() + ".ccgame";

        try
        {
            await svc.ExportAsync(original, tempFile);
            var restored = await svc.ImportAsync(tempFile);

            Assert.Equal(original.InitialFen, restored.InitialFen);
            Assert.Equal(original.Steps.Count, restored.Steps.Count);
            Assert.Equal(original.Steps[0].From, restored.Steps[0].From);
            Assert.Equal(original.Steps[0].To, restored.Steps[0].To);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ImportAsync_WithMissingFile_ShouldThrow()
    {
        var svc = new GameRecordService();
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => svc.ImportAsync("nonexistent_file_xyz.ccgame"));
    }
}
