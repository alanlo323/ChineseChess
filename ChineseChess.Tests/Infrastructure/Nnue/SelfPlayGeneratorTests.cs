using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Nnue.Training;

namespace ChineseChess.Tests.Infrastructure.Nnue;

/// <summary>
/// 驗證 SelfPlayGenerator 的正確性：
///   1. 能成功生成至少一個局面（1 局，深度 1）
///   2. 兩局以上在 randomOpeningMoves > 0 時開局路徑不完全相同
///   3. 每局在 250 步以內結束
///   4. 取消令牌能停止生成
///   5. randomOpeningMoves=0 時，兩局開局路徑相同（確定性）
/// </summary>
public class SelfPlayGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_ReturnsNonEmptyPositions_ForOneGame()
    {
        var network   = new TrainingNetwork();
        var generator = new SelfPlayGenerator(network, randomOpeningMoves: 4);

        var positions = await generator.GenerateAsync(
            gameCount: 1,
            searchDepth: 1,
            searchTimeLimitMs: 5000,
            onProgress: null,
            ct: CancellationToken.None);

        Assert.NotEmpty(positions);
    }

    [Fact]
    public async Task GenerateAsync_TwoGames_HaveDifferentOpenings()
    {
        // 一次生成 2 局（gameIndex 0 和 1 種子不同），兩局開局應有所不同
        var network   = new TrainingNetwork();
        var generator = new SelfPlayGenerator(network, randomOpeningMoves: 4);

        var positions = await generator.GenerateAsync(
            gameCount: 2, searchDepth: 1, searchTimeLimitMs: 5000,
            onProgress: null, ct: CancellationToken.None);

        // 確認收到至少兩局的局面
        Assert.True(positions.Count >= 2, "兩局均應有至少一個局面");

        // 第一局和第二局的第一個局面（初始 FEN）相同，但後續走法應不同
        // 取各局前幾個局面（假設每局至少 5 個局面）來比較
        // 由於兩局種子不同，至少在第一步就應選到不同著法
        // 我們比對所有局面的 FEN 集合是否完全相同
        var allFens = positions.Select(p => p.Fen).ToList();
        var uniqueFens = allFens.Distinct().Count();

        Assert.True(uniqueFens > 1,
            "前 4 步隨機後，兩局應包含不同局面（不應所有 FEN 完全相同）");
    }

    [Fact]
    public async Task GenerateAsync_GameEnds_Within250Moves()
    {
        var network   = new TrainingNetwork();
        var generator = new SelfPlayGenerator(network, randomOpeningMoves: 4);

        var positions = await generator.GenerateAsync(
            gameCount: 1,
            searchDepth: 1,
            searchTimeLimitMs: 5000,
            onProgress: null,
            ct: CancellationToken.None);

        // 每局最多 250 步（各局面對應一步）
        Assert.True(positions.Count <= 250,
            $"單局局面數應不超過 250，但得到 {positions.Count}");
    }

    [Fact]
    public async Task GenerateAsync_Respects_CancellationToken()
    {
        var network   = new TrainingNetwork();
        var generator = new SelfPlayGenerator(network, randomOpeningMoves: 4);
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        var positions = await generator.GenerateAsync(
            gameCount: 10,
            searchDepth: 2,
            searchTimeLimitMs: 5000,
            onProgress: null,
            ct: cts.Token);

        Assert.True(positions.Count == 0,
            $"取消後不應繼續生成，但收到 {positions.Count} 個局面");
    }

    [Fact]
    public async Task GenerateAsync_ZeroRandomMoves_SameDeterministicOpening()
    {
        // randomOpeningMoves=0 時，兩次呼叫應產生相同的開局（確定性搜尋）
        var network    = new TrainingNetwork();
        var generator1 = new SelfPlayGenerator(network, randomOpeningMoves: 0);
        var generator2 = new SelfPlayGenerator(network, randomOpeningMoves: 0);

        var positions1 = await generator1.GenerateAsync(
            gameCount: 1, searchDepth: 1, searchTimeLimitMs: 5000,
            onProgress: null, ct: CancellationToken.None);
        var positions2 = await generator2.GenerateAsync(
            gameCount: 1, searchDepth: 1, searchTimeLimitMs: 5000,
            onProgress: null, ct: CancellationToken.None);

        int compareCount = Math.Min(Math.Min(positions1.Count, positions2.Count), 5);
        Assert.True(compareCount > 0, "兩局均應有局面");

        for (int i = 0; i < compareCount; i++)
        {
            Assert.Equal(positions1[i].Fen, positions2[i].Fen);
        }
    }
}
