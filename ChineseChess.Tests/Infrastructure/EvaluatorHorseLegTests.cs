using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證評估函式中馬腳封堵懲罰的正確性。
/// 馬在四個可能的腳位（上/下/左/右）若被任何棋子封堵，則受到分數懲罰。
///
/// 測試設計原則：
///   - 封堵兵的 PST > 非封堵兵的 PST（確保無懲罰時封堵局面分較高，驗證 RED 階段確實失敗）
///   - 封堵位置選用橫向腳（col≠王的 col=4），避免改變王的暴露評估
///   - 每個測試僅改變封堵與否，其他條件完全相同
///
/// 棋盤索引：index = row * 9 + col（row 0 = 黑方底線，row 9 = 紅方底線）
/// 馬腳位置：馬在 (r,c)，腳位分別在 (r-1,c)、(r+1,c)、(r,c-1)、(r,c+1)
/// PawnTable row5：0,0,6,12,16,12,6,0,0（col3=12，col2=6，col5=12，col6=6）
/// </summary>
public class EvaluatorHorseLegTests
{
    private readonly HandcraftedEvaluator evaluator = new();

    // ─── 單腳封堵 ──────────────────────────────────────────────────────────

    [Fact]
    public void SingleLegBlocked_ScoresLowerThan_NoLegBlocked()
    {
        // 紅馬在 (5,4)，左腳 (5,3) 被紅兵封堵
        // vs 紅兵在非封堵位置 (5,2)
        //
        // 封堵兵 PST(5,3) = 12，非封堵兵 PST(5,2) = 6
        // 無懲罰：封堵局面比非封堵多 6 分 → Score(封堵) > Score(不封堵)
        // 有懲罰 -10：封堵局面多 6-10 = -4 分 → Score(封堵) < Score(不封堵) ✓
        var boardBlocked = new Board("k8/9/9/9/9/3PN4/9/9/9/4K4 w - - 0 1");
        var boardFree    = new Board("k8/9/9/9/9/2P1N4/9/9/9/4K4 w - - 0 1");

        int scoreBlocked = evaluator.Evaluate(boardBlocked);
        int scoreFree    = evaluator.Evaluate(boardFree);

        Assert.True(scoreBlocked < scoreFree,
            $"封堵 1 馬腳 ({scoreBlocked}) 應低於 0 腳封堵 ({scoreFree})");
    }

    // ─── 雙腳封堵 ──────────────────────────────────────────────────────────

    [Fact]
    public void TwoLegsBlocked_ScoresLowerThan_NoLegsBlocked()
    {
        // 紅馬在 (5,4)，左腳 (5,3) 和右腳 (5,5) 各一紅兵封堵
        // vs 兩個紅兵在非封堵位置 (5,2) 和 (5,6)
        //
        // 封堵兵合計 PST = 12+12 = 24，非封堵 = 6+6 = 12
        // 無懲罰：封堵局面多 12 分 → Score(封堵) > Score(不封堵)
        // 有懲罰 -10x2 = -20：封堵局面差 12-20 = -8 分 → Score(封堵) < Score(不封堵) ✓
        var boardBlocked = new Board("k8/9/9/9/9/3PNP3/9/9/9/4K4 w - - 0 1");
        var boardFree    = new Board("k8/9/9/9/9/2P1N1P2/9/9/9/4K4 w - - 0 1");

        int scoreBlocked = evaluator.Evaluate(boardBlocked);
        int scoreFree    = evaluator.Evaluate(boardFree);

        Assert.True(scoreBlocked < scoreFree,
            $"封堵 2 馬腳 ({scoreBlocked}) 應低於 0 腳封堵 ({scoreFree})");
    }

    // ─── 單調性：封堵越多分越低 ────────────────────────────────────────────

    [Fact]
    public void TwoLegsBlocked_ScoresLowerThan_OneLegBlocked()
    {
        // 2 腳封堵：左腳 (5,3) + 右腳 (5,5) 兩紅兵
        // 1 腳封堵：左腳 (5,3) 一紅兵 + 另一紅兵移至非封堵位 (5,6)
        //
        // 2 腳：PST(5,3)+PST(5,5) = 12+12 = 24，懲罰 -20 → 淨 +4
        // 1 腳：PST(5,3)+PST(5,6) = 12+6  = 18，懲罰 -10 → 淨 +8
        // Score(2腳) 比 Score(1腳) 少 4 → Score(2腳) < Score(1腳) ✓
        var board2Legs = new Board("k8/9/9/9/9/3PNP3/9/9/9/4K4 w - - 0 1");  // 左+右腳封
        var board1Leg  = new Board("k8/9/9/9/9/3PN1P2/9/9/9/4K4 w - - 0 1"); // 左腳封，右兵移至 (5,6)

        int score2 = evaluator.Evaluate(board2Legs);
        int score1 = evaluator.Evaluate(board1Leg);

        Assert.True(score2 < score1,
            $"封堵 2 腳 ({score2}) 應低於封堵 1 腳 ({score1})");
    }

    // ─── 黑方馬腳同樣受到懲罰 ──────────────────────────────────────────────

    [Fact]
    public void BlackHorseLeg_AlsoAppliesPenalty()
    {
        // 黑馬在 (4,4)，左腳 (4,3) 被黑兵封堵 vs 黑兵在非封堵位置 (4,2)
        // 從黑方視角（黑走），封堵馬腳應使分數更低
        //
        // 封堵黑兵 lookupIndex = 89-39=50，PST=12
        // 非封堵黑兵 lookupIndex = 89-38=51，PST=6
        // 無懲罰：封堵局面對黑方更好（+12 PST）→ Score(封堵) > Score(不封堵)
        // 有懲罰 -10：封堵局面差 12-10=+2，但從黑方視角變為 -2 → Score(封堵) < Score(不封堵) ✓
        var boardBlocked = new Board("4k4/9/9/9/3pn4/9/9/9/9/4K4 b - - 0 1");
        var boardFree    = new Board("4k4/9/9/9/2p1n4/9/9/9/9/4K4 b - - 0 1");

        int scoreBlocked = evaluator.Evaluate(boardBlocked);
        int scoreFree    = evaluator.Evaluate(boardFree);

        Assert.True(scoreBlocked < scoreFree,
            $"黑馬被封堵 ({scoreBlocked}) 應低於黑馬自由 ({scoreFree})");
    }
}
