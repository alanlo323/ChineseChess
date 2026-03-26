using ChineseChess.Domain.Enums;

namespace ChineseChess.Domain.Models;

/// <summary>
/// 殘局庫中單一局面的推算結論。
/// Depth：
///   Win  → 在 Depth 步內將死或困斃對方（步數越小越好）
///   Loss → 再撐 Depth 步後被將死或困斃（步數越大越好，表示走子方盡力拖延）
///   Draw → Depth 無意義，固定為 0
/// </summary>
public readonly record struct TablebaseEntry(TablebaseResult Result, int Depth)
{
    public static TablebaseEntry Unknown => new(TablebaseResult.Unknown, 0);
    public static TablebaseEntry Draw    => new(TablebaseResult.Draw,    0);

    public bool IsResolved => Result != TablebaseResult.Unknown;

    public override string ToString() => Result switch
    {
        TablebaseResult.Win  => $"必勝（{Depth} 步）",
        TablebaseResult.Loss => $"必負（{Depth} 步後）",
        TablebaseResult.Draw => "和棋",
        _                    => "未知",
    };
}
