using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.Tablebase;

/// <summary>
/// 各棋子在棋盤上的合法擺放格位（殘局庫位置列舉用）。
/// 索引計算：index = row * 9 + col（row 0 = 最頂排／黑方底線）
/// </summary>
internal static class ValidSquares
{
    // ── 將帥宮格 ────────────────────────────────────────────────────────
    // 紅帥宮：row 7-9，col 3-5
    public static readonly int[] RedKing =
        [66, 67, 68, 75, 76, 77, 84, 85, 86];

    // 黑將宮：row 0-2，col 3-5
    public static readonly int[] BlackKing =
        [3, 4, 5, 12, 13, 14, 21, 22, 23];

    // ── 仕／士位置 ──────────────────────────────────────────────────────
    // 紅仕：(7,3)=66, (7,5)=68, (8,4)=76, (9,3)=84, (9,5)=86
    public static readonly int[] RedAdvisor = [66, 68, 76, 84, 86];

    // 黑士：(0,3)=3, (0,5)=5, (1,4)=13, (2,3)=21, (2,5)=23
    public static readonly int[] BlackAdvisor = [3, 5, 13, 21, 23];

    // ── 相／象位置 ──────────────────────────────────────────────────────
    // 紅相：(5,2)=47, (5,6)=51, (7,0)=63, (7,4)=67, (7,8)=71, (9,2)=83, (9,6)=87
    public static readonly int[] RedElephant = [47, 51, 63, 67, 71, 83, 87];

    // 黑象：(0,2)=2, (0,6)=6, (2,0)=18, (2,4)=22, (2,8)=26, (4,2)=38, (4,6)=42
    public static readonly int[] BlackElephant = [2, 6, 18, 22, 26, 38, 42];

    // ── 兵／卒過河後的位置 ──────────────────────────────────────────────
    // 紅兵過河後（row 0-4，全 9 欄）= 45 格
    public static readonly int[] RedPawn = Enumerable.Range(0, 45).ToArray();

    // 黑卒過河後（row 5-9，全 9 欄）= 45 格
    public static readonly int[] BlackPawn = Enumerable.Range(45, 45).ToArray();

    // ── 自由棋子（車馬砲）：全棋盤 ────────────────────────────────────
    public static readonly int[] AnySquare = Enumerable.Range(0, 90).ToArray();

    // ── 依棋子類型取得合法格位 ──────────────────────────────────────────
    public static int[] GetSquares(PieceType type, PieceColor color) => type switch
    {
        PieceType.King     => color == PieceColor.Red ? RedKing     : BlackKing,
        PieceType.Advisor  => color == PieceColor.Red ? RedAdvisor  : BlackAdvisor,
        PieceType.Elephant => color == PieceColor.Red ? RedElephant : BlackElephant,
        PieceType.Pawn     => color == PieceColor.Red ? RedPawn     : BlackPawn,
        _                  => AnySquare,
    };
}
