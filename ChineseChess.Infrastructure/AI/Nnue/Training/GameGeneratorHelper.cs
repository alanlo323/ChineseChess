using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;

namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// VsHandcraftedGenerator 和 SelfPlayGenerator 共用的常數與輔助方法。
/// </summary>
internal static class GameGeneratorHelper
{
    internal const string InitialFen =
        "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    internal const int MaxMovesPerGame = 250;

    /// <summary>
    /// 依據局面狀態判斷對局結果（WDL）。
    /// IsCheckmate 只能在目標方輪次時呼叫，以 board.Turn 判斷被將死的一方。
    /// </summary>
    internal static float DetermineResult(IBoard board)
    {
        if (board.IsCheckmate(board.Turn))
            return board.Turn == PieceColor.Red ? 0.0f : 1.0f;
        return 0.5f;   // 和局或步數上限
    }
}
