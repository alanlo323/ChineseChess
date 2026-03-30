namespace ChineseChess.Domain.Constants;

/// <summary>遊戲評分相關常數。</summary>
public static class GameConstants
{
    /// <summary>
    /// 將死分數基準值。必勝局面的評分為 <c>MateScore - Depth</c>，
    /// 必負局面為 <c>-(MateScore - Depth)</c>。
    /// </summary>
    public const int MateScore = 20000;
}
