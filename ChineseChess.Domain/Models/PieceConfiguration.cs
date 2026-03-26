using ChineseChess.Domain.Enums;

namespace ChineseChess.Domain.Models;

/// <summary>
/// 殘局庫的子力組合定義。
/// 雙方的將/帥各一枚，此處只列額外棋子（不含將/帥）。
/// </summary>
public sealed record PieceConfiguration(
    string DisplayName,
    IReadOnlyList<PieceType> RedExtra,   // 紅方額外棋子（不含帥）
    IReadOnlyList<PieceType> BlackExtra) // 黑方額外棋子（不含將）
{
    /// <summary>估算局面總數（上界），供 UI 顯示警告用。</summary>
    public long EstimatedPositions
    {
        get
        {
            // 粗估：各棋子可放格數的乘積 × 2（紅先/黑先）
            long count = 9L * 9L * 2L; // 雙王 × 2 side
            foreach (var t in RedExtra)   count *= ApproxSquares(t);
            foreach (var t in BlackExtra) count *= ApproxSquares(t);
            return count;
        }
    }

    private static long ApproxSquares(PieceType type) => type switch
    {
        PieceType.Advisor  => 5,
        PieceType.Elephant => 7,
        PieceType.Pawn     => 45,   // 已過河的兵/卒
        _                  => 85,   // 車/馬/砲（保守估計，扣掉已佔格）
    };

    // ── 預設組合（按依存關係由簡到繁排列）──────────────────────────────

    public static readonly PieceConfiguration KingsOnly =
        new("帥 vs 將", [], []);

    public static readonly PieceConfiguration RookVsKing =
        new("帥車 vs 將", [PieceType.Rook], []);

    public static readonly PieceConfiguration RookVsAdvisor =
        new("帥車 vs 將士", [PieceType.Rook], [PieceType.Advisor]);

    public static readonly PieceConfiguration RookVsTwoAdvisors =
        new("帥車 vs 將士士", [PieceType.Rook], [PieceType.Advisor, PieceType.Advisor]);

    public static readonly PieceConfiguration RookVsAdvisorElephant =
        new("帥車 vs 將士象", [PieceType.Rook], [PieceType.Advisor, PieceType.Elephant]);

    public static readonly PieceConfiguration RookVsTwoAdvisorsTwoElephants =
        new("帥車 vs 將士士象象", [PieceType.Rook],
            [PieceType.Advisor, PieceType.Advisor, PieceType.Elephant, PieceType.Elephant]);

    public static readonly PieceConfiguration PawnVsTwoElephants =
        new("兵 vs 象象", [PieceType.Pawn],
            [PieceType.Elephant, PieceType.Elephant]);

    public static readonly PieceConfiguration TwoRooksVsTwoAdvisors =
        new("車車 vs 士士", [PieceType.Rook, PieceType.Rook],
            [PieceType.Advisor, PieceType.Advisor]);

    public static readonly PieceConfiguration PawnTwoElephantsVsTwoAdvisorsTwoElephants =
        new("兵相相 vs 士士象象",
            [PieceType.Pawn, PieceType.Elephant, PieceType.Elephant],
            [PieceType.Advisor, PieceType.Advisor, PieceType.Elephant, PieceType.Elephant]);

    /// <summary>所有預設組合（按複雜度排序）。</summary>
    public static readonly IReadOnlyList<PieceConfiguration> Presets =
    [
        KingsOnly,
        RookVsKing,
        RookVsAdvisor,
        RookVsTwoAdvisors,
        RookVsAdvisorElephant,
        RookVsTwoAdvisorsTwoElephants,
        PawnVsTwoElephants,
        TwoRooksVsTwoAdvisors,
        PawnTwoElephantsVsTwoAdvisorsTwoElephants,
    ];
}
