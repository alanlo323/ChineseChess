namespace ChineseChess.Domain.Constants;

/// <summary>遊戲評分相關常數。</summary>
public static class GameConstants
{
    /// <summary>
    /// 將死分數基準值。必勝局面的評分為 <c>MateScore - Depth</c>，
    /// 必負局面為 <c>-(MateScore - Depth)</c>。
    /// </summary>
    public const int MateScore = 20000;

    /// <summary>標準初始局面的 FEN 字串。</summary>
    public const string InitialPositionFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";
}
