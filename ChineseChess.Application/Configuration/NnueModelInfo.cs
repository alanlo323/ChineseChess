namespace ChineseChess.Application.Configuration;

/// <summary>
/// 已載入 NNUE 模型的元數據（執行期唯讀，不持久化）。
/// </summary>
public class NnueModelInfo
{
    /// <summary>模型檔完整路徑。</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>模型描述字串（來自 .nnue 檔頭）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>檔案大小（bytes）。</summary>
    public long FileSizeBytes { get; init; }

    /// <summary>載入時間戳記（UTC）。</summary>
    public DateTime LoadedAt { get; init; } = DateTime.UtcNow;

    /// <summary>格式化後的檔案大小字串（KB / MB）。</summary>
    public string FormattedFileSize => FileSizeBytes >= 1_048_576
        ? $"{FileSizeBytes / 1_048_576.0:F1} MB"
        : $"{FileSizeBytes / 1024.0:F1} KB";
}
