using ChineseChess.Domain.Entities;

namespace ChineseChess.Infrastructure.AI.Evaluators;

public interface IEvaluator
{
    int Evaluate(IBoard board);

    /// <summary>
    /// 快速評估：只含 Material + PST（含 GamePhase 插值）。
    /// 跳過王安全、機動力、兵型、炮威脅、馬腳、車壓制、棋子協同、空間控制。
    /// 用於 Lazy Evaluation 中的 Razor/Futility 剪枝預篩。
    /// </summary>
    int EvaluateFast(IBoard board);
}
