using ChineseChess.Application.Enums;
using ChineseChess.Application.Models;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using Xunit;

namespace ChineseChess.Tests.Application;

/// <summary>
/// WxfRepetitionJudge.Judge() 純函式邏輯的單元測試。
///
/// 歷史格式：history[0] 為種子（Cancel），其後為實際著法。
/// ZobristKey 用任意 ulong 值模擬位置識別。
/// 循環格式（4步/循環）：
///   A(種子) → B(紅) → C(黑) → D(紅) → A(黑) → B(紅) → C(黑) → D(紅) → A(黑)
///   ZobristKey：A=0, B=1, C=2, D=3
/// </summary>
public class WxfRepetitionJudgeTests
{
    // 固定 ZobristKey 供測試用
    private const ulong KeyA = 0UL;
    private const ulong KeyB = 1UL;
    private const ulong KeyC = 2UL;
    private const ulong KeyD = 3UL;

    private static MoveRecord Rec(ulong key, PieceColor turn, MoveClassification cls)
        => new MoveRecord
        {
            ZobristKey     = key,
            Turn           = turn,
            Move           = new Move(1, 2),
            Classification = cls,
            VictimSquare   = -1,
        };

    /// <summary>建立標準的雙循環歷史（種子 + 8 步）。</summary>
    /// <param name="redCls">紅方每步的分類。</param>
    /// <param name="blackCls">黑方每步的分類。</param>
    private static System.Collections.Generic.List<MoveRecord> BuildTwoCycleHistory(
        MoveClassification redCls, MoveClassification blackCls)
    {
        // 循環：A→B→C→D→A（紅走 A→B, C→D；黑走 B→C, D→A）
        return new System.Collections.Generic.List<MoveRecord>
        {
            Rec(KeyA, PieceColor.Red,   MoveClassification.Cancel), // 種子（初始局面 A）
            Rec(KeyB, PieceColor.Red,   redCls),                    // 紅：A→B（cycle1）
            Rec(KeyC, PieceColor.Black, blackCls),                  // 黑：B→C
            Rec(KeyD, PieceColor.Red,   redCls),                    // 紅：C→D
            Rec(KeyA, PieceColor.Black, blackCls),                  // 黑：D→A（第 2 次 A）
            Rec(KeyB, PieceColor.Red,   redCls),                    // 紅：A→B（cycle2）
            Rec(KeyC, PieceColor.Black, blackCls),                  // 黑：B→C
            Rec(KeyD, PieceColor.Red,   redCls),                    // 紅：C→D
            Rec(KeyA, PieceColor.Black, blackCls),                  // 黑：D→A（第 3 次 A，當前）
        };
    }

    // ─── 基本裁決 ─────────────────────────────────────────────────────────

    [Fact]
    public void Judge_BothIdle_ReturnsDraw()
    {
        var history = BuildTwoCycleHistory(MoveClassification.Idle, MoveClassification.Idle);
        Assert.Equal(RepetitionVerdict.Draw, WxfRepetitionJudge.Judge(history));
    }

    [Fact]
    public void Judge_RedCheck_BlackIdle_ReturnsBlackWins()
    {
        var history = BuildTwoCycleHistory(MoveClassification.Check, MoveClassification.Idle);
        Assert.Equal(RepetitionVerdict.BlackWins, WxfRepetitionJudge.Judge(history));
    }

    [Fact]
    public void Judge_BlackChase_RedIdle_ReturnsRedWins()
    {
        var history = BuildTwoCycleHistory(MoveClassification.Idle, MoveClassification.Chase);
        Assert.Equal(RepetitionVerdict.RedWins, WxfRepetitionJudge.Judge(history));
    }

    [Fact]
    public void Judge_BothCheck_ReturnsDraw()
    {
        var history = BuildTwoCycleHistory(MoveClassification.Check, MoveClassification.Check);
        Assert.Equal(RepetitionVerdict.Draw, WxfRepetitionJudge.Judge(history));
    }

    [Fact]
    public void Judge_BothChase_ReturnsDraw()
    {
        var history = BuildTwoCycleHistory(MoveClassification.Chase, MoveClassification.Chase);
        Assert.Equal(RepetitionVerdict.Draw, WxfRepetitionJudge.Judge(history));
    }

    [Fact]
    public void Judge_RedCheck_BlackChase_ReturnsBlackWins()
    {
        // Check(3) > Chase(2)，紅方違規更重
        var history = BuildTwoCycleHistory(MoveClassification.Check, MoveClassification.Chase);
        Assert.Equal(RepetitionVerdict.BlackWins, WxfRepetitionJudge.Judge(history));
    }

    // ─── Cancel 打斷重複鏈 ────────────────────────────────────────────────

    [Fact]
    public void Judge_CancelInCycle_ReturnsNone()
    {
        // 在循環中放入一個 Cancel（吃子），重複鏈被打斷
        var history = new System.Collections.Generic.List<MoveRecord>
        {
            Rec(KeyA, PieceColor.Red,   MoveClassification.Cancel), // 種子
            Rec(KeyB, PieceColor.Red,   MoveClassification.Idle),
            Rec(KeyC, PieceColor.Black, MoveClassification.Cancel), // 黑方吃子！
            Rec(KeyD, PieceColor.Red,   MoveClassification.Idle),
            Rec(KeyA, PieceColor.Black, MoveClassification.Idle),
            Rec(KeyB, PieceColor.Red,   MoveClassification.Idle),
            Rec(KeyC, PieceColor.Black, MoveClassification.Idle),
            Rec(KeyD, PieceColor.Red,   MoveClassification.Idle),
            Rec(KeyA, PieceColor.Black, MoveClassification.Idle),
        };
        Assert.Equal(RepetitionVerdict.None, WxfRepetitionJudge.Judge(history));
    }

    // ─── 不足 3 次重複 ─────────────────────────────────────────────────────

    [Fact]
    public void Judge_OnlyTwoOccurrences_ReturnsNone()
    {
        // 只有 2 次出現（種子 + 1 次），不夠 3 次
        var history = new System.Collections.Generic.List<MoveRecord>
        {
            Rec(KeyA, PieceColor.Red,   MoveClassification.Cancel), // 種子
            Rec(KeyB, PieceColor.Red,   MoveClassification.Idle),
            Rec(KeyC, PieceColor.Black, MoveClassification.Idle),
            Rec(KeyD, PieceColor.Red,   MoveClassification.Idle),
            Rec(KeyA, PieceColor.Black, MoveClassification.Idle),   // 第 2 次 A
        };
        Assert.Equal(RepetitionVerdict.None, WxfRepetitionJudge.Judge(history));
    }

    [Fact]
    public void Judge_EmptyHistory_ReturnsNone()
    {
        Assert.Equal(RepetitionVerdict.None,
            WxfRepetitionJudge.Judge(new System.Collections.Generic.List<MoveRecord>()));
    }

    // ─── 兵橫移（Idle）不打斷重複鏈 ──────────────────────────────────────

    [Fact]
    public void Judge_PawnLateralMove_Idle_DoesNotBreakCycle()
    {
        // 兵橫移被分類為 Idle，不是 Cancel，不打斷重複鏈
        var history = BuildTwoCycleHistory(MoveClassification.Idle, MoveClassification.Idle);
        // 把循環中的某步改成 Idle（兵橫移模擬）
        // 確認結果仍是 Draw（Idle 不打斷，也不升級違規）
        Assert.Equal(RepetitionVerdict.Draw, WxfRepetitionJudge.Judge(history));
    }

    // ─── 種子條目不影響裁決 ────────────────────────────────────────────────

    [Fact]
    public void Judge_SeedCancelIsNotIncludedInViolationAnalysis()
    {
        // 種子是 Cancel，但它在 secondMatch 位置，不在分析範圍內
        // 驗證紅方連續 Check 的裁決不受種子影響
        var history = BuildTwoCycleHistory(MoveClassification.Check, MoveClassification.Idle);
        Assert.Equal(RepetitionVerdict.BlackWins, WxfRepetitionJudge.Judge(history));
    }

    // ─── 捉與將軍互斥（皮卡魚規則）─────────────────────────────────────────

    [Fact]
    public void Judge_RedChases_BlackNonPerpetualCheck_ShouldBeDraw()
    {
        // 紅方每步 Chase，黑方序列中有 Check（但非長將）→ 因序列有 Check，Chase 不定義 → Draw
        // 皮卡魚規則：「捉：僅在該循環序列中沒有任何一步將軍時才定義」
        var history = new System.Collections.Generic.List<MoveRecord>
        {
            Rec(KeyA, PieceColor.Red,   MoveClassification.Cancel), // 種子
            Rec(KeyB, PieceColor.Red,   MoveClassification.Chase),  // 紅：Chase（cycle1）
            Rec(KeyC, PieceColor.Black, MoveClassification.Check),  // 黑：Check（序列中有將）
            Rec(KeyD, PieceColor.Red,   MoveClassification.Chase),  // 紅：Chase
            Rec(KeyA, PieceColor.Black, MoveClassification.Idle),   // 黑：Idle（第 2 次 A）
            Rec(KeyB, PieceColor.Red,   MoveClassification.Chase),  // 紅：Chase（cycle2）
            Rec(KeyC, PieceColor.Black, MoveClassification.Idle),   // 黑：Idle
            Rec(KeyD, PieceColor.Red,   MoveClassification.Chase),  // 紅：Chase
            Rec(KeyA, PieceColor.Black, MoveClassification.Idle),   // 黑：Idle（第 3 次 A）
        };
        // 序列中有 Check → 紅方 Chase 不定義 → 雙方均 Idle → Draw
        Assert.Equal(RepetitionVerdict.Draw, WxfRepetitionJudge.Judge(history));
    }
}
