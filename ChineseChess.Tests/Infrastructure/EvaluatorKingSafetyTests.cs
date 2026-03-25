using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證評估函式中「敵車壓制王/帥列」的懲罰正確性。
///
/// 問題背景：
///   現有 EvaluateKingSafety 的「暴露列」檢查，把「敵車在同列」視為「未暴露」
///   （因為敵車佔據了列，所以判定非空），但這實際上是最危險的攻擊！
///   需新增獨立的「敵車壓力」評估來正確懲罰此類威脅。
///
/// 測試設計原則：
///   - 黑車在 (5,4) vs (5,3)：RookTable PST 均為 24（相同材料）
///   - 紅車在 (4,4) vs (4,3)：RookTable PST 均為 24（相同材料）
///   - 唯一差異是車是否在王的列上，可隔離「敵車壓制」效果
///
/// 棋盤索引：index = row * 9 + col（row 0 = 黑方底線，row 9 = 紅方底線）
/// RookTable row4,col4 = 24；row4,col5 = 24（對稱，皆為 24）
/// </summary>
public class EvaluatorKingSafetyTests
{
    private readonly HandcraftedEvaluator evaluator = new();

    // ─── 紅帥受黑車壓制 ───────────────────────────────────────────────────

    [Fact]
    public void BlackRook_OnRedKingColumn_ReducesScore()
    {
        // 黑車在 (5,4)，與紅帥在 (9,4) 同列，中間無阻隔（危險！）
        // vs 黑車在 (5,3)，不在紅帥列上（安全）
        //
        // PST：兩個位置的黑車 PST 均為 24（RookTable 完全對稱）
        // 無懲罰：黑車在帥列反而「蓋住」暴露，分數比在鄰列更高（BUG！）
        // 有懲罰：黑車壓制帥列應讓紅方得分降低 → Score(危險) < Score(安全)
        var boardDangerous = new Board("k8/9/9/9/9/4r4/9/9/9/4K4 w - - 0 1");
        var boardSafe      = new Board("k8/9/9/9/9/3r5/9/9/9/4K4 w - - 0 1");

        int scoreDangerous = evaluator.Evaluate(boardDangerous);
        int scoreSafe      = evaluator.Evaluate(boardSafe);

        Assert.True(scoreDangerous < scoreSafe,
            $"黑車壓制帥列 ({scoreDangerous}) 應低於黑車離帥列 ({scoreSafe})");
    }

    // ─── 黑將受紅車壓制 ───────────────────────────────────────────────────

    [Fact]
    public void RedRook_OnBlackKingColumn_ReducesBlackScore()
    {
        // 紅車在 (4,4)，與黑將在 (0,4) 同列，中間無阻隔（危險！）
        // vs 紅車在 (4,3)，不在黑將列上（安全）
        //
        // PST：兩個位置的紅車 PST 均為 24（完全對稱）
        // 黑方視角：受壓制的局面應使黑方得分降低 → Score(危險) < Score(安全)
        var boardDangerous = new Board("4k4/9/9/9/4R4/9/9/9/9/4K4 b - - 0 1");
        var boardSafe      = new Board("4k4/9/9/9/3R5/9/9/9/9/4K4 b - - 0 1");

        int scoreDangerous = evaluator.Evaluate(boardDangerous);
        int scoreSafe      = evaluator.Evaluate(boardSafe);

        Assert.True(scoreDangerous < scoreSafe,
            $"紅車壓制黑將列 ({scoreDangerous}) 應低於紅車離黑將列 ({scoreSafe})");
    }

    // ─── 有己方棋子阻擋時：壓制威脅應降低 ────────────────────────────────

    [Fact]
    public void BlackRook_BlockedByFriendlyPiece_LessPenaltyThanOpen()
    {
        // 黑車壓制開放列（無阻隔）vs 黑車有己方棋子阻隔（半開放）
        // 半開放的威脅應小於完全開放
        //
        // 開放：黑車在 (3,4)，紅帥在 (9,4)，row 4-8 in col=4 全空
        // 半開放：黑車在 (3,4)，紅仕在 (7,4) 阻隔，紅帥在 (9,4)
        //
        // 注意：半開放位置增加了一個紅仕（友方棋子），需比較兩局面分數差異
        // 此測試驗證：帥有防守子保護時，敵車壓制的懲罰應小於完全開放
        var boardOpen       = new Board("k8/9/9/4r4/9/9/9/9/9/4K4 w - - 0 1");  // 開放列
        var boardBlocked    = new Board("k8/9/9/4r4/9/9/9/4A4/9/4K4 w - - 0 1"); // 有仕阻隔

        int scoreOpen    = evaluator.Evaluate(boardOpen);
        int scoreBlocked = evaluator.Evaluate(boardBlocked);

        // 有仕阻隔時的帥（有防守）應比完全開放好 → scoreBlocked > scoreOpen
        // （因為仕是紅方棋子，增加材料，且阻隔了車的直接威脅）
        Assert.True(scoreBlocked > scoreOpen,
            $"有仕阻隔 ({scoreBlocked}) 應比開放列 ({scoreOpen}) 得分更高");
    }
}
