namespace ChineseChess.Application.Models;

/// <summary>殘局庫生成進度回報。</summary>
public sealed record TablebaseGenerationProgress(
    string Phase,
    long ProcessedPositions,
    long TotalPositions,
    int WinCount,
    int LossCount,
    int DrawCount,
    bool IsComplete,
    string? ErrorMessage = null)
{
    public double ProgressFraction =>
        TotalPositions > 0 ? (double)ProcessedPositions / TotalPositions : 0.0;

    public string Summary =>
        IsComplete
            ? $"完成：共 {TotalPositions:N0} 局面，勝 {WinCount:N0}，負 {LossCount:N0}，和 {DrawCount:N0}"
            : $"{Phase}：{ProcessedPositions:N0} / {TotalPositions:N0}";
}
