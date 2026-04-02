using System.Text.RegularExpressions;

namespace ChineseChess.Application.Configuration;

/// <summary>
/// 已載入的 NNUE 模型資訊（唯讀記錄型別）。
/// 作為 Application 層的 DTO，不包含 Infrastructure 的 INnueNetwork 參考。
/// </summary>
public record LoadedNnueModelInfo
{
    public string   Id            { get; init; } = Guid.NewGuid().ToString();
    public string   FilePath      { get; init; } = string.Empty;
    public string   FileName      { get; init; } = string.Empty;
    public string   Description   { get; init; } = string.Empty;
    public long     FileSizeBytes { get; init; }
    public DateTime LoadedAt      { get; init; } = DateTime.Now;
    /// <summary>從檔名解析的 Elo（如 elo_1102_xxx.nnue → 1102），無法解析時為 null。</summary>
    public int?     Elo           { get; init; }

    public string FormattedFileSize => FileSizeBytes >= 1_048_576
        ? $"{FileSizeBytes / 1_048_576.0:F1} MB"
        : $"{FileSizeBytes / 1024.0:F1} KB";

    public string EloText => Elo.HasValue ? Elo.Value.ToString() : "?";

    public string DisplayName => $"[{FileName}] Elo:{EloText}";

    /// <summary>從檔名解析 Elo，格式：elo_1102_xxx.nnue。無符合回傳 null。</summary>
    public static int? ParseEloFromFileName(string fileName)
    {
        var match = EloPattern.Match(fileName);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static readonly Regex EloPattern = new(@"elo_(\d+)_", RegexOptions.IgnoreCase);
}
