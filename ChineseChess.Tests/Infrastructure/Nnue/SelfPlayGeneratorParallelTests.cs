using ChineseChess.Infrastructure.AI.Nnue.Training;

namespace ChineseChess.Tests.Infrastructure.Nnue;

/// <summary>
/// 驗證 SelfPlayGenerator 並行模式（parallelism &gt; 1）的正確性：
///   1. 返回完整局數（所有 gameCount 局均完成）
///   2. 取消令牌能正確傳播並停止生成
///   3. 進度回報的 GamesCompleted 不超過 gameCount
/// </summary>
[Collection("NnueGeneration")]
public class SelfPlayGeneratorParallelTests
{
    [Fact]
    public async Task GenerateAsync_Parallelism2_ReturnsAllGames()
    {
        var network   = new TrainingNetwork();
        var generator = new SelfPlayGenerator(network, randomOpeningMoves: 2, parallelism: 2);

        var positions = await generator.GenerateAsync(
            gameCount: 4,
            searchDepth: 1,
            searchTimeLimitMs: 5000,
            onProgress: null,
            ct: CancellationToken.None);

        // 4 局均應完成，每局至少一個局面
        Assert.True(positions.Count >= 4,
            $"並行模式應完成所有 4 局，共至少 4 個局面，實際得到 {positions.Count}");
    }

    [Fact]
    public async Task GenerateAsync_Parallelism2_RespectsCancel()
    {
        var network   = new TrainingNetwork();
        var generator = new SelfPlayGenerator(network, randomOpeningMoves: 2, parallelism: 2);

        using var cts = new CancellationTokenSource();
        cts.Cancel();  // 立即取消

        // 已取消的 token 應讓 GenerateAsync 快速結束，不拋未處理例外
        var ex = await Record.ExceptionAsync(() => generator.GenerateAsync(
            gameCount: 10,
            searchDepth: 1,
            searchTimeLimitMs: 5000,
            onProgress: null,
            ct: cts.Token));

        Assert.True(ex is null || ex is OperationCanceledException,
            $"已取消時應無例外或僅 OperationCanceledException，但得到 {ex?.GetType().Name}");
    }

    [Fact]
    public async Task GenerateAsync_Parallelism2_ProgressNeverExceedsGameCount()
    {
        const int gameCount = 4;
        var network   = new TrainingNetwork();
        var generator = new SelfPlayGenerator(network, randomOpeningMoves: 2, parallelism: 2);

        var completedCounts = new System.Collections.Concurrent.ConcurrentBag<int>();

        await generator.GenerateAsync(
            gameCount: gameCount,
            searchDepth: 1,
            searchTimeLimitMs: 5000,
            onProgress: p => completedCounts.Add(p.GamesCompleted),
            ct: CancellationToken.None);

        Assert.All(completedCounts, count =>
            Assert.True(count <= gameCount,
                $"GamesCompleted={count} 不應超過 gameCount={gameCount}"));

        Assert.Equal(gameCount, completedCounts.Count);
    }
}
