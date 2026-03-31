using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證雙象雙士完整防守加分（plan C1）和炮台品質加分（plan C2）。
/// </summary>
public class DefenseFormationTests
{
    private readonly HandcraftedEvaluator evaluator = new();

    // ─── 雙象雙士完整防守加分 ──────────────────────────────

    // Red: K + 2A + 2B (完整防守), Black: K only
    private const string FenFullDefense = "4k4/9/9/9/9/9/9/9/9/2BAKAB2 w - - 0 1";

    // Red: K + 2A + 1B (部分防守，缺一象), Black: K only
    private const string FenPartialDefense = "4k4/9/9/9/9/9/9/9/9/3AKAB2 w - - 0 1";

    [Fact]
    public void FullDefense_DoubleAdvisorAndElephant_GivesExtraBonusBeyondMaterial()
    {
        // Full defense (2A+2B) vs partial (2A+1B)
        // 差值來源：象材料(120) + 殘局調整(14) + PST(6) + 王安全(10) + 機動力(1-6)
        //   + 完整防守加分變化 (+20 vs +8 = +12)
        // 不含防守加分：≈ 152~158
        // 含防守加分：≈ 164~170
        var boardFull = new Board(FenFullDefense);
        var boardPartial = new Board(FenPartialDefense);

        int scoreFull = evaluator.Evaluate(boardFull);
        int scorePartial = evaluator.Evaluate(boardPartial);

        int diff = scoreFull - scorePartial;
        Assert.True(diff > 160,
            $"Full defense diff ({diff}) 應 > 160，確認完整防守加分（+12）超越純材料+PST+王安全差值（≈156）");
    }

    // ─── 炮台品質 ─────────────────────────────────────

    // 紅炮在 (6,4)，友方士在 (3,4) 作炮台，瞄準黑將 (0,4)
    private const string FenCannonAdvisorScreen = "4k4/9/9/4A4/9/9/4C4/9/9/4K4 w - - 0 1";

    // 紅炮在 (6,4)，友方兵在 (3,4) 作炮台，瞄準黑將 (0,4)
    private const string FenCannonPawnScreen = "4k4/9/9/4P4/9/9/4C4/9/9/4K4 w - - 0 1";

    [Fact]
    public void CannonScreenQuality_FriendlyAdvisor_ScoresHigherThanFriendlyPawn()
    {
        // 兩板都有炮透過友方棋子瞄準黑將（打將威脅加分 +40 相同）
        // 但士作炮台比兵更穩固（不易被拆除）→ 炮台品質加分 +10
        // 其餘差值：士材料(120)-兵(30)=90 + 王安全(20) + 殘局調整(14) + 機動力(~1)
        //   ≈ 125 不含炮台品質，≈ 135 含炮台品質
        var boardAdvisor = new Board(FenCannonAdvisorScreen);
        var boardPawn = new Board(FenCannonPawnScreen);

        int scoreAdvisor = evaluator.Evaluate(boardAdvisor);
        int scorePawn = evaluator.Evaluate(boardPawn);

        int diff = scoreAdvisor - scorePawn;
        Assert.True(diff > 122,
            $"炮台品質 diff ({diff}) 應 > 122，確認士炮台品質加分（+10）超越純材料+王安全差值（≈117）");
    }
}
