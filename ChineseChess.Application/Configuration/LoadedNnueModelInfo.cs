namespace ChineseChess.Application.Configuration;

/// <summary>
/// 已載入的 NNUE 模型資訊（唯讀記錄型別）。
/// 作為 Application 層的 DTO，不包含 Infrastructure 的 INnueNetwork 參考。
/// </summary>
public record LoadedNnueModelInfo
{
    public string Id           { get; init; } = Guid.NewGuid().ToString();
    public string FilePath     { get; init; } = string.Empty;
    public string FileName     { get; init; } = string.Empty;
    public string Description  { get; init; } = string.Empty;
    public long FileSizeBytes  { get; init; }
    public DateTime LoadedAt   { get; init; } = DateTime.Now;

    public string FormattedFileSize => FileSizeBytes >= 1_048_576
        ? $"{FileSizeBytes / 1_048_576.0:F1} MB"
        : $"{FileSizeBytes / 1024.0:F1} KB";

    public string DisplayName => string.IsNullOrEmpty(Description)
        ? FileName
        : $"{FileName}（{Description}）";
}
