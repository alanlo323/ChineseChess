using ChineseChess.Application.Configuration;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using ChineseChess.Infrastructure.AI.Nnue.Evaluator;
using ChineseChess.Infrastructure.AI.Nnue.Network;

namespace ChineseChess.Tests.Infrastructure.Nnue;

/// <summary>
/// CompositeEvaluator 測試：
///   1. NNUE 未載入時，結果與 HandcraftedEvaluator 完全一致
///   2. NNUE 未載入時，累加器鉤子不拋出例外（no-op）
/// </summary>
public class CompositeEvaluatorTests
{
    private const string InitialFen =
        "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ── 空實作 INnueNetwork（未載入狀態）───────────────────────────────

    private sealed class UnloadedNetwork : INnueNetwork
    {
        public bool IsLoaded => false;
        public NnueModelInfo? ModelInfo => null;
        public NnueWeights? Weights => null;
        public Task LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public void Unload() { }
        public int Evaluate(IBoard board, NnueAccumulator accumulator)
            => throw new InvalidOperationException("NNUE 未載入，不應呼叫 Evaluate");
    }

    // ── 測試 ────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_WhenNnueNotLoaded_MatchesHandcraftedEvaluator()
    {
        var board      = new Board(InitialFen);
        var composite  = new CompositeEvaluator(new UnloadedNetwork());
        var reference  = new HandcraftedEvaluator();

        int compositeResult  = composite.Evaluate(board);
        int referenceResult  = reference.Evaluate(board);

        Assert.Equal(referenceResult, compositeResult);
    }

    [Fact]
    public void EvaluateFast_WhenNnueNotLoaded_MatchesHandcraftedEvaluator()
    {
        var board     = new Board(InitialFen);
        var composite = new CompositeEvaluator(new UnloadedNetwork());
        var reference = new HandcraftedEvaluator();

        int compositeResult = composite.EvaluateFast(board);
        int referenceResult = reference.EvaluateFast(board);

        Assert.Equal(referenceResult, compositeResult);
    }

    [Fact]
    public void AccumulatorHooks_WhenNnueNotLoaded_DoNotThrow()
    {
        // 走法：紅兵 index 54 → 45
        const int from = 54;
        const int to   = 45;
        var board     = new Board(InitialFen);
        var composite = new CompositeEvaluator(new UnloadedNetwork());
        composite.RefreshAccumulator(board);  // no-op，不拋出

        var movedPiece    = board.GetPiece(from);
        var capturedPiece = board.GetPiece(to);
        var move          = new Move(from, to);

        board.MakeMove(move);
        composite.OnMakeMove(board, move, movedPiece, capturedPiece);  // no-op

        board.UnmakeMove(move);
        composite.OnUndoMove(board, move);  // no-op

        // 到達此處代表均無例外
        Assert.True(true);
    }

    [Fact]
    public void Evaluate_WhenNnueNotLoaded_ReturnsSymmetricScoreForInitialPosition()
    {
        // 初始對稱局面下，紅方手番，評分應接近 0（±小額 tempo 加成）
        var board     = new Board(InitialFen);
        var composite = new CompositeEvaluator(new UnloadedNetwork());

        int score = composite.Evaluate(board);

        // 對稱局面的評分不應大於一個車的價值（600）
        Assert.InRange(score, -600, 600);
    }
}
