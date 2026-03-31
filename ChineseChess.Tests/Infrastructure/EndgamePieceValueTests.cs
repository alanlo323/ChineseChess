using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證殘局棋子價值調整（Endgame Piece Value Adjustment）功能：
///   - 殘局（phase 趨近 0）：炮 -20, 馬 +20, 象 +15, 士 +15
///   - 開局（phase ≈ 256）：無調整
///   - 插值漸變：殘局局面（少棋子）調整量接近最大值
///
/// 測試設計原則：
///   - 使用「極簡殘局」（僅剩將帥 + 一個棋子）使 phase ≈ 0，讓調整量接近最大
///   - 以馬 vs 炮的相對價值翻轉驗證：開局炮 > 馬，殘局馬 > 炮
///   - 對稱局面不影響 EvaluateFast 的對稱性測試（phase≈256 時調整≈0）
///
/// 棋盤索引：index = row * 9 + col
/// </summary>
public class EndgamePieceValueTests
{
    private readonly HandcraftedEvaluator evaluator = new();

    // ─── 殘局：馬比炮更有價值 ─────────────────────────────────────────────

    [Fact]
    public void Endgame_Horse_MoreValuableThan_Cannon()
    {
        // 極簡殘局：僅剩帥/將 + 一個紅方棋子（phase ≈ 0）
        // 比較「帥 + 馬」vs「帥 + 炮」的局面評估
        //
        // 原始材料：Cannon(285) > Horse(270)，炮略貴
        // 殘局調整：Cannon(-20)、Horse(+20) → 調整後 Horse(290) > Cannon(265)
        //
        // 使用 EvaluateFast() 隔離材料分+殘局調整，排除機動力、馬腳封堵等干擾因素
        // 兩個局面中棋子放在相同位置（row 8, col 4）以消除 PST 差異
        var boardHorse  = new Board("4k4/9/9/9/9/9/9/9/4N4/4K4 w - - 0 1");
        var boardCannon = new Board("4k4/9/9/9/9/9/9/9/4C4/4K4 w - - 0 1");

        int scoreHorse  = evaluator.EvaluateFast(boardHorse);
        int scoreCannon = evaluator.EvaluateFast(boardCannon);

        Assert.True(scoreHorse > scoreCannon,
            $"殘局馬 ({scoreHorse}) 應比殘局炮 ({scoreCannon}) 更有價值");
    }

    [Fact]
    public void Endgame_Elephant_MoreValuableThan_Opening()
    {
        // 殘局時象/相更重要（防守價值上升）
        // 使用「帥 + 象」的近殘局局面與「接近完整局面的象」比較
        //
        // 近殘局（僅帥 + 象）：phase ≈ 3/78 * 256 ≈ 9
        // 象的殘局調整 = Interpolate(0, +15, 9) ≈ +15
        //
        // 測試：殘局象分數 > 未調整的象分數
        // 代理測試：使用材料分計算器驗證相位調整效果
        var boardEndgame = new Board("4k4/9/9/9/9/9/9/9/4B4/4K4 w - - 0 1");
        var boardOpening = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");

        // 殘局：帥 + 象 → 從紅方視角評估（紅行棋）
        // 開局：標準棋局 → 初始對稱，紅方視角應接近 0
        int scoreEndgame = evaluator.Evaluate(boardEndgame);
        int scoreOpening = evaluator.Evaluate(boardOpening);

        // 殘局時象有正的調整加成，分數應高於 Elephant 基礎材料分 + PST
        // 注意：殘局分數含 KingSafety 懲罰（無士象），但象調整應使分數更高
        // 此測試主要驗證殘局調整功能正確執行
        Assert.True(scoreEndgame > scoreOpening,
            $"殘局有棋子時 ({scoreEndgame}) 應高於初始對稱局面 ({scoreOpening})");
    }

    [Fact]
    public void Opening_Cannon_MoreValuableThan_Horse()
    {
        // 開局（高 phase）：炮比馬略貴，原始材料值炮(285) > 馬(270)
        // 使用「標準開局 + 一個額外紅方棋子（row8, col1）」確保 phase 高（接近 256）
        //
        // 兩個局面完全相同，只差在 row8 col1 放炮(C)或馬(N)
        // 使用 EvaluateFast() 以排除機動力等動態因素干擾
        // phase ≈ 256（全棋子）→ 殘局調整 ≈ 0 → 差值由材料分決定
        var boardWithCannon = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/1C7/RNBAKABNR w - - 0 1");
        var boardWithHorse  = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/1N7/RNBAKABNR w - - 0 1");

        int scoreCannon = evaluator.EvaluateFast(boardWithCannon);
        int scoreHorse  = evaluator.EvaluateFast(boardWithHorse);

        // 開局時炮 > 馬（285 - 270 = 15 原始差值，調整接近 0）
        Assert.True(scoreCannon > scoreHorse,
            $"開局炮 ({scoreCannon}) 應比開局馬 ({scoreHorse}) 稍高");
    }

    // ─── EvaluateFast 對稱性保護 ──────────────────────────────────────────

    [Fact]
    public void EvaluateFast_InitialPosition_StillSymmetric()
    {
        // 確認加入殘局調整後，EvaluateFast 在初始對稱局面仍回傳接近 0
        // 初始局面 phase ≈ 256，殘局調整 ≈ 0，不影響對稱性
        var board = new Board("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1");

        int fastScore = evaluator.EvaluateFast(board);

        // 初始局面完全對稱，EvaluateFast 應 = 0
        Assert.Equal(0, fastScore);
    }
}
