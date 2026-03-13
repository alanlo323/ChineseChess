using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 GamePhase 棋局階段偵測功能：
///   - 完整材料 = 開局（接近 256）
///   - 少量材料 = 殘局（接近 0）
///   - 相位值在有效範圍 [0, 256] 內
///   - 評估插值：開局重 PST，殘局重材料
/// </summary>
public class GamePhaseTests
{
    // ─── 相位計算 ─────────────────────────────────────────────────────────

    [Fact]
    public void GamePhase_FullMaterial_ReturnsHighPhase()
    {
        // 完整開局布置，相位值應接近 256（開局）
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        int phase = GamePhase.Calculate(board);

        // 開局相位應 >= 200（接近 256）
        Assert.True(phase >= 180, $"開局相位應 >= 180，實際 = {phase}");
        Assert.True(phase <= 256, $"相位不應超過 256，實際 = {phase}");
    }

    [Fact]
    public void GamePhase_OnlyKings_ReturnsZero()
    {
        // 只有雙方將帥，完全殘局
        var board = new Board("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        int phase = GamePhase.Calculate(board);

        Assert.Equal(0, phase);
    }

    [Fact]
    public void GamePhase_AlwaysInValidRange()
    {
        // 相位值必須在 [0, 256] 範圍內
        var boards = new[]
        {
            new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1"),
            new Board("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1"),
            new Board("4k4/4a4/9/9/9/9/9/9/4A4/4K4 w - - 0 1"),
            new Board("r3k3r/9/9/9/9/9/9/9/9/R3K3R w - - 0 1"),
        };

        foreach (var board in boards)
        {
            int phase = GamePhase.Calculate(board);
            Assert.True(phase >= 0 && phase <= 256,
                $"相位 {phase} 超出 [0, 256] 範圍");
        }
    }

    [Fact]
    public void GamePhase_MoreMaterial_ReturnsHigherPhase()
    {
        // 材料越多，相位越高（越接近開局）
        var moreMatBoard = new Board("r3k3r/9/2c3c2/9/9/9/9/2C3C2/9/R3K3R w - - 0 1");
        var lessMatBoard = new Board("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");

        int phaseMore = GamePhase.Calculate(moreMatBoard);
        int phaseLess = GamePhase.Calculate(lessMatBoard);

        Assert.True(phaseMore > phaseLess,
            $"更多材料 ({phaseMore}) 應有更高相位，殘局 ({phaseLess})");
    }

    // ─── 評估插值 ─────────────────────────────────────────────────────────

    [Fact]
    public void GamePhase_Interpolate_OpeningPhaseReturnsOpeningValue()
    {
        // 完全開局（phase=256）應回傳開局評估值
        int result = GamePhase.Interpolate(openingScore: 100, endgameScore: 50, phase: 256);
        Assert.Equal(100, result);
    }

    [Fact]
    public void GamePhase_Interpolate_EndgamePhaseReturnsEndgameValue()
    {
        // 完全殘局（phase=0）應回傳殘局評估值
        int result = GamePhase.Interpolate(openingScore: 100, endgameScore: 50, phase: 0);
        Assert.Equal(50, result);
    }

    [Fact]
    public void GamePhase_Interpolate_MidpointReturnsAverage()
    {
        // 中間相位（phase=128）應回傳兩者的插值
        int result = GamePhase.Interpolate(openingScore: 100, endgameScore: 0, phase: 128);
        // 128/256 = 0.5，所以 100*0.5 + 0*0.5 = 50（整數除法可能有 ±1 誤差）
        Assert.True(result >= 49 && result <= 51,
            $"中間相位插值應約為 50，實際 = {result}");
    }

    // ─── 評估器整合 ───────────────────────────────────────────────────────

    [Fact]
    public void HandcraftedEvaluator_WithPhaseInterpolation_StillReturnsConsistentScore()
    {
        // 評估器在相位插值後應仍回傳一致的分數（相同棋盤兩次評估結果相同）
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");
        var evaluator = new HandcraftedEvaluator();

        int first = evaluator.Evaluate(board);
        int second = evaluator.Evaluate(board);

        Assert.Equal(first, second);
    }
}
