namespace ChineseChess.Domain.Enums;

/// <summary>殘局庫結論：從「輪到走子方」角度表示。</summary>
public enum TablebaseResult
{
    /// <summary>尚未推算出結論。</summary>
    Unknown = 0,

    /// <summary>走子方在最優著法下必勝。</summary>
    Win = 1,

    /// <summary>走子方在任何著法下均必負。</summary>
    Loss = 2,

    /// <summary>雙方最優著法均為和棋。</summary>
    Draw = 3,
}
