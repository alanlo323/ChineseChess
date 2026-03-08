namespace ChineseChess.Application.Enums;

/// <summary>
/// 遊戲結局列舉。
/// </summary>
public enum GameResult
{
    /// <summary>遊戲進行中。</summary>
    InProgress,

    /// <summary>紅方獲勝（將死或困斃黑方）。</summary>
    RedWin,

    /// <summary>黑方獲勝（將死或困斃紅方）。</summary>
    BlackWin,

    /// <summary>和棋（三次重覆局面或六十步無吃子）。</summary>
    Draw
}
