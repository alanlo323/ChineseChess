using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Nnue.Training;

namespace ChineseChess.Tests.Infrastructure.Nnue;

/// <summary>
/// 驗證 TrainingNetworkInferenceView 的推論正確性與執行緒安全：
///   1. 回傳的分數與 TrainingNetwork.EvaluateToScore 相同（邏輯等價）
///   2. 多個 InferenceView 並行對同一局面求值，結果一致且無例外（執行緒安全）
/// </summary>
public class TrainingNetworkInferenceViewTests
{
    private const string InitialFen =
        "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    private const string MidgameFen =
        "r1bakabr1/9/1cn1c1n2/p1p1p1p1p/9/2P6/P3P1P1P/1C2C1N2/9/RNBAKAB1R w - - 0 1";

    [Theory]
    [InlineData(InitialFen)]
    [InlineData(MidgameFen)]
    public void EvaluateToScore_MatchesOriginalNetwork(string fen)
    {
        var network = new TrainingNetwork();
        var view    = new TrainingNetworkInferenceView(network);
        var board   = new Board(fen);

        int expected = network.EvaluateToScore(board);
        int actual   = view.EvaluateToScore(board);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task EvaluateToScore_ConcurrentCalls_DoNotInterfere()
    {
        // 4 個 InferenceView 同時對同一局面求值，全部結果應相同
        var network = new TrainingNetwork();
        var board   = new Board(InitialFen);
        int expected = network.EvaluateToScore(board);

        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var view = new TrainingNetworkInferenceView(network);
            // 對同一局面多次呼叫，確認快取重用不影響結果
            int r1 = view.EvaluateToScore(new Board(InitialFen));
            int r2 = view.EvaluateToScore(new Board(MidgameFen));
            int r3 = view.EvaluateToScore(new Board(InitialFen));  // 再回到初始局面
            return (r1, r2, r3);
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        // 所有 task 對同一 FEN 的分數應一致
        Assert.All(results, r => Assert.Equal(expected, r.r1));
        Assert.All(results, r => Assert.Equal(expected, r.r3));
        // 中盤局面的結果也應互相一致
        Assert.Single(results.Select(r => r.r2).Distinct());
    }
}
