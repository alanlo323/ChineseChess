using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.AI.Evaluators;

/// <summary>
/// 棋局階段偵測工具類別。
/// 根據場上棋子的材料值計算棋局相位（0 = 殘局，256 = 開局）。
/// 相位值用於在開局評估和殘局評估之間進行插值（Tapered Evaluation）。
///
/// 計算方式：
///   - 各棋子依類型賦予相位貢獻值
///   - 累計所有棋子的相位貢獻，除以最大相位（clamp 至 [0, 256]）
///   - 帥/將不計入相位（永遠在場）
/// </summary>
public static class GamePhase
{
    // 最大相位值（完整開局材料時）
    public const int MaxPhase = 256;

    // 各棋子類型的相位貢獻值（以開局雙方各有的數量估算）
    // 雙方合計：車x2=48，馬x2=20，炮x2=20，仕x2=8，象x2=8，兵x5=10 ≈ 114 → 縮放到 256
    private static readonly int[] PhaseContribution =
    {
        0,  // None
        0,  // King（帥/將 永遠在場，不計入相位）
        1,  // Advisor（仕/士）
        1,  // Elephant（相/象）
        3,  // Horse（傌/馬）
        8,  // Rook（俥/車）
        4,  // Cannon（炮/砲）
        1,  // Pawn（兵/卒）
    };

    // 完整開局的最大相位原始分（雙方各一套棋子）
    // 雙方：仕x4=4，象x4=4，馬x4=12，車x4=32，炮x4=16，兵x10=10 → 78
    private const int FullPhaseSumMax = 78;

    /// <summary>
    /// 計算棋局相位，回傳 0（殘局）到 256（開局）之間的整數。
    /// </summary>
    public static int Calculate(IBoard board)
    {
        int phaseSum = 0;
        for (int i = 0; i < 90; i++)
        {
            var piece = board.GetPiece(i);
            if (piece.IsNone || piece.Type == PieceType.King) continue;
            phaseSum += PhaseContribution[(int)piece.Type];
        }

        // 縮放到 [0, 256]
        if (phaseSum >= FullPhaseSumMax) return MaxPhase;
        return phaseSum * MaxPhase / FullPhaseSumMax;
    }

    /// <summary>
    /// 根據棋局相位在開局評估和殘局評估之間進行線性插值。
    /// phase = 256 時回傳 openingScore；phase = 0 時回傳 endgameScore。
    /// </summary>
    public static int Interpolate(int openingScore, int endgameScore, int phase)
    {
        // 線性插值：result = opening * phase/256 + endgame * (256-phase)/256
        return (openingScore * phase + endgameScore * (MaxPhase - phase)) / MaxPhase;
    }
}
