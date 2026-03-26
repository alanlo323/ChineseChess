using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Models;
using ChineseChess.Infrastructure.Tablebase;
using System.IO;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>殘局庫 FEN 序列化 / 反序列化測試。</summary>
public class TablebaseSerializerTests
{
    // ── 匯出後匯入（round-trip）─────────────────────────────────────────

    [Fact]
    public async Task ExportThenImport_ShouldPreserveAllEntries()
    {
        // 生成最小的殘局庫
        var config = PieceConfiguration.KingsOnly;
        var storage = new TablebaseStorage();
        var analyzer = new RetrogradAnalyzer(storage, subTablebases: null);
        analyzer.Analyze(config);

        var original = storage.GetAllEntries().ToDictionary(e => e.Key, e => e.Value);
        Assert.NotEmpty(original);

        // 匯出到臨時檔案
        var tempFile = Path.GetTempFileName();
        try
        {
            await TablebaseSerializer.ExportAsync(storage, config, tempFile);

            // 匯入到新的 storage
            var imported = new TablebaseStorage();
            int count = await TablebaseSerializer.ImportAsync(imported, tempFile);

            Assert.Equal(original.Count, count);
            Assert.Equal(original.Count, imported.TotalPositions);

            // 每個條目的結論應一致
            foreach (var (hash, entry) in original)
            {
                var importedEntry = imported.Query(hash);
                Assert.Equal(entry.Result, importedEntry.Result);
                Assert.Equal(entry.Depth, importedEntry.Depth);
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Export_ShouldCreateReadableFile()
    {
        var config = PieceConfiguration.KingsOnly;
        var storage = new TablebaseStorage();
        var analyzer = new RetrogradAnalyzer(storage, subTablebases: null);
        analyzer.Analyze(config);

        var tempFile = Path.GetTempFileName();
        try
        {
            await TablebaseSerializer.ExportAsync(storage, config, tempFile);

            var lines = await File.ReadAllLinesAsync(tempFile);
            // 首行應為 # 開頭的注釋
            Assert.True(lines.Length > 0 && lines[0].StartsWith('#'));
            // 應有資料行
            var dataLines = lines.Where(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l)).ToList();
            Assert.NotEmpty(dataLines);

            // 每行格式：FEN 空格 W/L/D 空格 深度
            foreach (var line in dataLines.Take(5))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.True(parts.Length >= 2, $"行格式不正確：{line}");
                Assert.Contains(parts[^2], new[] { "W", "L", "D" });
                Assert.True(int.TryParse(parts[^1], out _), $"深度應為整數：{parts[^1]}");
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Import_EmptyFile_ShouldReturnZero()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "# 空殘局庫\n");
            var storage = new TablebaseStorage();
            int count = await TablebaseSerializer.ImportAsync(storage, tempFile);
            Assert.Equal(0, count);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Import_InvalidLines_ShouldSkipGracefully()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // 混入無效行
            await File.WriteAllTextAsync(tempFile,
                "# 殘局庫測試\n" +
                "not_a_fen W 5\n" +      // 無效 FEN（應跳過）
                "invalid line\n" +        // 格式不對（應跳過）
                "3k5/9/9/9/9/9/9/9/9/3KR4 b - - 0 1 W 3\n");  // 有效行

            var storage = new TablebaseStorage();
            int count = await TablebaseSerializer.ImportAsync(storage, tempFile);

            // 只有最後一行有效
            Assert.Equal(1, count);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
