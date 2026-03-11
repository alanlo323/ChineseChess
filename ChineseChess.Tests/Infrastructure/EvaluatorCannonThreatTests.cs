using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證評估函式中炮（砲）威脅加分的正確性。
/// 炮透過「炮台」（任意一子）跳吃對方棋子，若瞄準對方將帥則應給予較高的威脅加分。
///
/// 測試設計原則：
///   - 炮台棋子選用 PST=0 的位置（避免位置分干擾），確保兩局面材料完全等價
///   - 炮台在炮的攻擊線上 vs 炮台在非攻擊線上（其他條件完全相同）
///
/// 棋盤索引：index = row * 9 + col（row 0 = 黑方底線，row 9 = 紅方底線）
///
/// 黑兵在 (3,4) 從黑方 PST：lookupIndex=89-31=58，PawnTable[58]=row6,col4=0
/// 黑兵在 (3,3) 從黑方 PST：lookupIndex=89-30=59，PawnTable[59]=row6,col5=0
/// ⇒ 兩個位置的黑兵 PST 完全相同（皆為 0），可隔離炮威脅效果
/// </summary>
public class EvaluatorCannonThreatTests
{
    private static readonly HandcraftedEvaluator evaluator = new();

    // ─── 紅炮威脅黑將 ─────────────────────────────────────────────────────

    [Fact]
    public void RedCannon_CannonKingThreat_ScoresHigherThan_NoThreat()
    {
        // 紅炮在 (5,4)，黑兵在 (3,4) 為炮台，黑將在 (0,4)
        // 炮 → 炮台(3,4) → 黑將(0,4)：成立威脅 → 應有加分
        //
        // 對照：黑兵在 (3,3)（非炮台位置），炮在 col=4 方向無炮台無法打將
        // 兩個黑兵 PST 均為 0，材料完全等價，唯一差異是炮台是否在攻擊線上
        //
        // 無威脅加分：分數相等（材料、PST、王安全均相同）
        // 有威脅加分：Score(威脅) > Score(非威脅) ✓
        var boardThreat   = new Board("4k4/9/9/4p4/9/4C4/9/9/9/4K4 w - - 0 1");
        var boardNoThreat = new Board("4k4/9/9/3p5/9/4C4/9/9/9/4K4 w - - 0 1");

        int scoreThreat   = evaluator.Evaluate(boardThreat);
        int scoreNoThreat = evaluator.Evaluate(boardNoThreat);

        Assert.True(scoreThreat > scoreNoThreat,
            $"架炮打將 ({scoreThreat}) 應高於無威脅 ({scoreNoThreat})");
    }

    // ─── 黑炮威脅紅帥 ─────────────────────────────────────────────────────

    [Fact]
    public void BlackCannon_CannonKingThreat_ScoresHigherThan_NoThreat()
    {
        // 黑炮在 (4,4)，紅兵在 (6,4) 為炮台，紅帥在 (9,4)
        // 黑炮 → 炮台(6,4) → 紅帥(9,4)：成立威脅 → 對黑方有加分
        //
        // 對照：紅兵在 (6,3)（非炮台位置）
        // 紅兵 PST：row 6 全為 0，兩位置 PST 完全相等
        var boardThreat   = new Board("4k4/9/9/9/4c4/9/4P4/9/9/4K4 b - - 0 1");
        var boardNoThreat = new Board("4k4/9/9/9/4c4/9/3P5/9/9/4K4 b - - 0 1");

        int scoreThreat   = evaluator.Evaluate(boardThreat);
        int scoreNoThreat = evaluator.Evaluate(boardNoThreat);

        Assert.True(scoreThreat > scoreNoThreat,
            $"黑炮架炮打帥 ({scoreThreat}) 應高於無威脅 ({scoreNoThreat})");
    }

    // ─── 炮台後無目標：不應有威脅加分 ────────────────────────────────────

    [Fact]
    public void Cannon_ScreenExistsButNoEnemyBehind_NoThreatBonus()
    {
        // 紅炮在 (5,4)，黑兵在 (3,4) 為炮台，但 col=4 的 row 0-2 全空（黑將在 (0,0)）
        // 炮台後無敵子 → 無威脅加分
        //
        // 對照：黑兵在 (3,3)（非炮台），黑將同樣在 (0,0)
        // 兩個黑兵 PST 均為 0（row 6 from black view），材料完全等價
        // 兩局面均無炮威脅加分 → 分數應完全相同
        var boardScreenNoTarget = new Board("k8/9/9/4p4/9/4C4/9/9/9/4K4 w - - 0 1");
        var boardNoScreen       = new Board("k8/9/9/3p5/9/4C4/9/9/9/4K4 w - - 0 1");

        int scoreWithScreen = evaluator.Evaluate(boardScreenNoTarget);
        int scoreNoScreen   = evaluator.Evaluate(boardNoScreen);

        // 無炮威脅時兩局面分數應相等
        Assert.Equal(scoreNoScreen, scoreWithScreen);
    }
}
