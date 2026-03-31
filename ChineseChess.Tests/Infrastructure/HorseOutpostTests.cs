using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證馬前哨加分（C1）和車入底線加分（C2）。
/// 跨河且機動力充足的馬應獲得前哨加分；
/// 車深入對方底部兩排應獲得入底線加分。
/// </summary>
public class HorseOutpostTests
{
    private readonly HandcraftedEvaluator evaluator = new();

    // 馬在對方領土 (3,4)，有足夠機動力 → 前哨加分
    private const string FenHorseAdvanced = "3k5/9/9/4N4/9/9/9/9/9/4K4 w - - 0 1";

    // 馬在己方領土 (6,4)，同等機動力 → 無前哨加分
    private const string FenHorseHome = "3k5/9/9/9/9/9/4N4/9/9/4K4 w - - 0 1";

    [Fact]
    public void HorseOutpost_CrossedRiver_ScoresHigherThanHomeHorse()
    {
        // 跨河馬（row=3 < 5）+ mobility ≥ 2 應比同等配置的本方馬得分更高
        // 差值來源：HorseTable[31]=20 vs HorseTable[58]=16（PST 差） + 前哨加分 15
        var boardAdvanced = new Board(FenHorseAdvanced);
        var boardHome     = new Board(FenHorseHome);

        int scoreAdvanced = evaluator.Evaluate(boardAdvanced);
        int scoreHome     = evaluator.Evaluate(boardHome);

        Assert.True(scoreAdvanced > scoreHome,
            $"跨河馬 ({scoreAdvanced}) 應比本方馬 ({scoreHome}) 得分更高（前哨加分）");
        int diff = scoreAdvanced - scoreHome;
        Assert.True(diff > 8,
            $"分數差 ({diff}) 應 > 8，確認前哨加分（+15）生效，而非僅 PST 差值（≈+2）");
    }

    [Fact]
    public void HorseOutpost_NotCrossedRiver_NoOutpostBonus()
    {
        // 剛過中線（row=5）的馬不算跨河前哨，應比深入前線（row=3）得分低
        // FenHorseMid: 馬在 (5,4) = 河界上方，紅方視角剛好在界上不算跨河
        const string fenHorseMid = "3k5/9/9/9/9/4N4/9/9/9/4K4 w - - 0 1";

        var boardAdvanced = new Board(FenHorseAdvanced);
        var boardMid      = new Board(fenHorseMid);

        int scoreAdvanced = evaluator.Evaluate(boardAdvanced);
        int scoreMid      = evaluator.Evaluate(boardMid);

        // 跨河馬應得分更高（前哨加分 + 更好的 PST）
        Assert.True(scoreAdvanced > scoreMid,
            $"跨河馬 ({scoreAdvanced}) 應比界上馬 ({scoreMid}) 得分更高");
        int diff2 = scoreAdvanced - scoreMid;
        Assert.True(diff2 > 10,
            $"分數差 ({diff2}) 應 > 10，確認前哨加分（+15）讓跨河馬顯著優於界上馬（界上馬無前哨加分）");
    }

    // ─── C2：車入底線加分 ───────────────────────────────────────────────────

    // 紅車在對方底部第二排 (1,4)，深入敵陣 → 入底線加分
    private const string FenRookPenetrating = "3k5/4R4/9/9/9/9/9/9/9/4K4 w - - 0 1";

    // 紅車在己方底部 (8,4) → 無加分
    private const string FenRookHome = "3k5/9/9/9/9/9/9/9/4R4/4K4 w - - 0 1";

    [Fact]
    public void RookPenetration_EnemyBackRow_ScoresHigherThanHomeRook()
    {
        // 車在對方底部兩排（row ≤ 1）應比同等的本方車得分更高
        var boardPenetrating = new Board(FenRookPenetrating);
        var boardHome        = new Board(FenRookHome);

        int scorePenetrating = evaluator.Evaluate(boardPenetrating);
        int scoreHome        = evaluator.Evaluate(boardHome);

        Assert.True(scorePenetrating > scoreHome,
            $"入底線車 ({scorePenetrating}) 應比本方車 ({scoreHome}) 得分更高（入底線加分）");
        int diff = scorePenetrating - scoreHome;
        Assert.True(diff > 14,
            $"分數差 ({diff}) 應 > 14，確認入底線加分（+12）生效（PST≈4 + SpaceControl≈7 + C2加分 = ≈23）");
    }
}
