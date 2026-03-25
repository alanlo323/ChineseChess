using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Nnue.Training;

namespace ChineseChess.Tests.Infrastructure.Nnue;

/// <summary>
/// 驗證 VsHandcraftedGenerator 的正確性：
///   1. 能成功生成至少一個局面（1 局，深度 1）
///   2. 所有回傳的 FEN 可解析為合法的 Board
///   3. 所有 Result 值屬於合法 WDL 集合 {0.0, 0.5, 1.0}
///   4. 取消令牌能停止生成
///   5. 每完成一局至少呼叫一次進度回調
/// </summary>
[Collection("NnueGeneration")]
public class VsHandcraftedGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_ReturnsNonEmptyPositions_ForOneGame()
    {
        var network   = new TrainingNetwork();
        var generator = new VsHandcraftedGenerator(network);

        var positions = await generator.GenerateAsync(
            gameCount: 1,
            searchDepth: 1,
            searchTimeLimitMs: 5000,
            onProgress: null,
            ct: CancellationToken.None);

        Assert.NotEmpty(positions);
    }

    [Fact]
    public async Task GenerateAsync_AllFens_ParseableAsBoard()
    {
        var network   = new TrainingNetwork();
        var generator = new VsHandcraftedGenerator(network);

        var positions = await generator.GenerateAsync(
            gameCount: 1,
            searchDepth: 1,
            searchTimeLimitMs: 5000,
            onProgress: null,
            ct: CancellationToken.None);

        foreach (var pos in positions)
        {
            // 不應拋出例外
            var board = new Board(pos.Fen);
            Assert.NotNull(board);
        }
    }

    [Fact]
    public async Task GenerateAsync_AllResults_AreValidWdl()
    {
        var network   = new TrainingNetwork();
        var generator = new VsHandcraftedGenerator(network);

        var positions = await generator.GenerateAsync(
            gameCount: 1,
            searchDepth: 1,
            searchTimeLimitMs: 5000,
            onProgress: null,
            ct: CancellationToken.None);

        float[] validWdl = [0.0f, 0.5f, 1.0f];
        foreach (var pos in positions)
        {
            Assert.Contains(pos.Result, validWdl);
        }
    }

    [Fact]
    public async Task GenerateAsync_Respects_CancellationToken()
    {
        var network   = new TrainingNetwork();
        var generator = new VsHandcraftedGenerator(network);
        using var cts = new CancellationTokenSource();

        // 取消後應快速停止，不拋出例外
        await cts.CancelAsync();

        var positions = await generator.GenerateAsync(
            gameCount: 10,
            searchDepth: 2,
            searchTimeLimitMs: 5000,
            onProgress: null,
            ct: cts.Token);

        // 已取消，應回傳空或極少局面
        Assert.True(positions.Count == 0,
            $"取消後不應繼續生成，但收到 {positions.Count} 個局面");
    }

    [Fact]
    public async Task GenerateAsync_ReportsProgress_PerGame()
    {
        var network   = new TrainingNetwork();
        var generator = new VsHandcraftedGenerator(network);
        int progressCallCount = 0;

        await generator.GenerateAsync(
            gameCount: 2,
            searchDepth: 1,
            searchTimeLimitMs: 5000,
            onProgress: _ => progressCallCount++,
            ct: CancellationToken.None);

        // 每完成一局至少呼叫一次 onProgress
        Assert.True(progressCallCount >= 2,
            $"2 局應至少 2 次進度回調，但只有 {progressCallCount} 次");
    }
}
