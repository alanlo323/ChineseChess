namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// 對局生成階段的進度快照，供 UI 層綁定顯示。
/// </summary>
public sealed class GameGenerationProgress
{
    public int GamesCompleted     { get; init; }
    public int GamesTarget        { get; init; }
    public int PositionsCollected { get; init; }
    public string? Message        { get; init; }
    public bool IsGenerating      { get; init; }
}
