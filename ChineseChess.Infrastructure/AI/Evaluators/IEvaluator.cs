using ChineseChess.Domain.Entities;

namespace ChineseChess.Infrastructure.AI.Evaluators;

public interface IEvaluator
{
    int Evaluate(IBoard board);
}
