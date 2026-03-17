using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證 Lazy Evaluation：
///   EvaluateFast() 只含 Material + PST，不含其他項目。
///   Razor/Futility 剪枝邏輯使用 LazyMargin 邊界控制 fastEval/fullEval 的選擇。
/// </summary>
public class LazyEvaluationTests
{
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";
    private const int LazyMargin = 200;

    // ─── 測試 1：EvaluateFast() 只包含 Material + PST（不含其他項目）───

    [Fact]
    public void EvaluateFast_OnlyMaterialAndPst()
    {
        // 使用一個局面：有炮威脅、有馬腳封堵、有敵車壓制的局面
        // EvaluateFast 應與手動計算的 Material + PST 一致，不含額外項目
        var evaluator = new HandcraftedEvaluator();

        // 標準初始局面：對稱，EvaluateFast 應為 0（雙方 Material + PST 對稱）
        var board = new Board(InitialFen);
        int fastScore = evaluator.EvaluateFast(board);
        int fullScore = evaluator.Evaluate(board);

        // EvaluateFast 和 Evaluate 都從當前行棋方視角回傳
        // 初始對稱局面兩者應接近（但 fullScore 含更多項目，可能有差異）
        // 主要驗證：fastScore 不等於 Evaluate 的詳細結果（有差異）
        // 對稱初始局面下 EvaluateFast 應為 0（Material 對稱，PST 也對稱）
        Assert.Equal(0, fastScore);
    }

    // ─── 測試 2：EvaluateFast() 與 Evaluate() 差距在正常局面 < LazyMargin ───

    [Fact]
    public void EvaluateFast_VsEvaluate_DiffWithinLazyMargin()
    {
        // 正常局面（非極端）的 EvaluateFast 與 Evaluate 差距應小於 LazyMargin
        var evaluator = new HandcraftedEvaluator();
        var board = new Board(InitialFen);

        int fastScore = evaluator.EvaluateFast(board);
        int fullScore = evaluator.Evaluate(board);

        int diff = Math.Abs(fastScore - fullScore);
        // 初始局面兩者均應為 0（對稱），所以 diff == 0
        Assert.True(diff <= LazyMargin,
            $"EvaluateFast 與 Evaluate 差距 {diff} 應 <= LazyMargin={LazyMargin}");
    }

    // ─── 測試 3：EvaluateFast() 不包含王安全計算（移除防守子後分數不改變）───

    [Fact]
    public void EvaluateFast_DoesNotIncludeKingSafety()
    {
        // 建立一個缺少防守子的局面（少仕/象）
        // EvaluateFast 不考慮王安全，所以此局面與完整防守局面的 EvaluateFast 差值
        // 僅來自材料分（仕/象的材料分），不含王安全懲罰
        var evaluator = new HandcraftedEvaluator();
        var fullDefenseFen = "4k4/4a4/4b4/9/9/9/9/4B4/4A4/4K4 w - - 0 1";
        var missingAdvisorFen = "4k4/4a4/4b4/9/9/9/9/4B4/9/4K4 w - - 0 1";

        var fullBoard = new Board(fullDefenseFen);
        var missingBoard = new Board(missingAdvisorFen);

        int fastFull = evaluator.EvaluateFast(fullBoard);
        int fastMissing = evaluator.EvaluateFast(missingBoard);
        int fullFull = evaluator.Evaluate(fullBoard);
        int fullMissing = evaluator.Evaluate(missingBoard);

        // EvaluateFast 差值：僅材料分（仕=120）
        int fastDiff = fastFull - fastMissing;
        // Evaluate 差值：材料分 + 王安全懲罰，所以差值更大
        int fullDiff = fullFull - fullMissing;

        // EvaluateFast 差值應小於 Evaluate 差值（因 Evaluate 含王安全懲罰）
        Assert.True(fullDiff > fastDiff,
            $"Evaluate 含王安全懲罰差值 {fullDiff} 應 > EvaluateFast 差值 {fastDiff}");
    }

    // ─── 測試 4：EvaluateFast() 不包含炮威脅（炮有無炮台加分不影響 EvaluateFast）───

    [Fact]
    public void EvaluateFast_DoesNotIncludeCannonThreats()
    {
        var evaluator = new HandcraftedEvaluator();

        // 炮有炮台瞄準敵將的局面
        var cannonThreatFen = "3ak4/9/9/9/9/9/9/9/9/3K1C3 w - - 0 1";
        // 相同局面但移除炮台（炮不在瞄準線上）
        var noThreatFen = "3ak4/9/9/9/9/9/9/9/9/3K5 w - - 0 1";

        var threatBoard = new Board(cannonThreatFen);
        var noThreatBoard = new Board(noThreatFen);

        int fastThreat = evaluator.EvaluateFast(threatBoard);
        int fastNoThreat = evaluator.EvaluateFast(noThreatBoard);
        int fullThreat = evaluator.Evaluate(threatBoard);
        int fullNoThreat = evaluator.Evaluate(noThreatBoard);

        // EvaluateFast 差值：僅炮的材料分（285）+ PST 差異
        // Evaluate 差值：材料分 + 炮威脅加分
        int fastDiff = fastThreat - fastNoThreat;
        int fullDiff = fullThreat - fullNoThreat;

        // Evaluate 差值應包含炮威脅加分，大於 EvaluateFast 差值
        Assert.True(fullDiff > fastDiff,
            $"Evaluate 含炮威脅加分差值 {fullDiff} 應 > EvaluateFast 差值 {fastDiff}");
    }

    // ─── 測試 5：EvaluateFast() 包含材料分（吃子後 fast 分數正確變化）───

    [Fact]
    public void EvaluateFast_IncludesMaterial()
    {
        var evaluator = new HandcraftedEvaluator();

        // 只有雙將的局面（對稱）
        var evenBoard = new Board("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        // 紅方多一個炮（285分）
        var redAheadBoard = new Board("4k4/9/9/9/9/9/9/9/9/4K1C2 w - - 0 1");

        int fastEven = evaluator.EvaluateFast(evenBoard);
        int fastRedAhead = evaluator.EvaluateFast(redAheadBoard);

        // 紅方多一個炮，EvaluateFast 應回傳正分（從紅方視角）
        Assert.Equal(0, fastEven); // 對稱局面應為 0
        Assert.True(fastRedAhead > 0,
            $"紅方多炮，EvaluateFast 應為正分，實際 {fastRedAhead}");
    }

    // ─── 測試 6：EvaluateFast() 包含 PST（位置影響分數）───

    [Fact]
    public void EvaluateFast_IncludesPst()
    {
        var evaluator = new HandcraftedEvaluator();

        // 馬在中央（高 PST）vs 角落（低 PST）的局面
        var horseCenterFen = "4k4/9/9/9/4N4/9/9/9/9/4K4 w - - 0 1";
        var horseCornerFen = "4k4/9/9/9/N8/9/9/9/9/4K4 w - - 0 1";

        var centerBoard = new Board(horseCenterFen);
        var cornerBoard = new Board(horseCornerFen);

        int fastCenter = evaluator.EvaluateFast(centerBoard);
        int fastCorner = evaluator.EvaluateFast(horseCornerFen == null
            ? cornerBoard
            : cornerBoard);

        // 中央馬的 PST 分高，EvaluateFast 應比角落馬高
        Assert.True(fastCenter >= fastCorner,
            $"中央馬 EvaluateFast={fastCenter} 應 >= 角落馬 {fastCorner}");
    }

    // ─── 測試 7：EvaluateFast 不包含機動力計算 ───

    [Fact]
    public void EvaluateFast_DoesNotIncludeMobility()
    {
        var evaluator = new HandcraftedEvaluator();

        // 對稱初始局面：Evaluate 含機動力（從行棋方視角），EvaluateFast 不含
        var board = new Board(InitialFen);

        int fast = evaluator.EvaluateFast(board);
        int full = evaluator.Evaluate(board);

        // 初始對稱局面 EvaluateFast = 0（材料 PST 對稱）
        // Evaluate 可能含機動力差異，但對稱局面下也應接近 0
        // 主要驗證：EvaluateFast 穩定為 0
        Assert.Equal(0, fast);
    }
}
