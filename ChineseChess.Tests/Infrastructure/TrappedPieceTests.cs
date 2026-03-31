using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證被困棋子懲罰（Trapped Piece Penalty）。
/// 馬的所有腳位都被封堵（0 mobility）時，會觸發一次性懲罰，
/// 比「逐腳扣分」機制更嚴厲地反映被困馬對局勢的影響。
/// </summary>
public class TrappedPieceTests
{
    private readonly HandcraftedEvaluator evaluator = new();

    // --- 基本局面說明 ---
    // 馬在 (5,4)，3 個腳位被封 → down 方向仍自由 → mobility=2
    // "4k4/9/9/9/4P4/3PNP3/9/9/9/4K4 w - - 0 1"
    //   Row 4: P at (4,4) = up leg blocked
    //   Row 5: P at (5,3)=left leg, N at (5,4)=horse, P at (5,5)=right leg
    //   Row 6: empty → down leg (6,4) free → can jump (7,3) and (7,5)
    private const string FenPartialBlock =
        "4k4/9/9/9/4P4/3PNP3/9/9/9/4K4 w - - 0 1";

    // 同上但加一個兵在 (6,4) 封堵最後一個腳位 → mobility=0（被困）
    // "4k4/9/9/9/4P4/3PNP3/4P4/9/9/4K4 w - - 0 1"
    //   PawnTable[row6] = 0 → 新兵無 PST 貢獻，方便隔離被困懲罰的效果
    private const string FenFullBlock =
        "4k4/9/9/9/4P4/3PNP3/4P4/9/9/4K4 w - - 0 1";

    [Fact]
    public void TrappedHorse_ZeroMobility_ScoresLowerDespiteExtraPawn()
    {
        // FenPartialBlock：K + N + 3P，馬 mobility=2（3 腳封堵，down 方向自由）
        // FenFullBlock  ：K + N + 4P，馬 mobility=0（4 腳全封）
        //
        // FenFullBlock 多 1 個兵（+30 材料），但被困懲罰（-50）+ 腳封差值（-10）
        // 應使 FenFullBlock 整體得分 < FenPartialBlock
        var boardFree    = new Board(FenPartialBlock);
        var boardTrapped = new Board(FenFullBlock);

        int scoreFree    = evaluator.Evaluate(boardFree);
        int scoreTrapped = evaluator.Evaluate(boardTrapped);

        Assert.True(scoreFree > scoreTrapped,
            $"3腳封堵馬 ({scoreFree}) 即使少一個兵，也應比被完全困住的馬 ({scoreTrapped}) 評分更高");
    }

    [Fact]
    public void TrappedHorse_AddingFinalBlockingPawn_DecreasesScore()
    {
        // 加入第 4 個封腳兵後，分數「應下降」（非上升）
        // 若無被困懲罰：預期 +30（兵材料）- 10（多一腳懲罰）= +20（分數上升）
        // 有被困懲罰（-50）：預期 +30 - 10 - 50 = -30（分數下降）
        // 驗證：scoreFree - scoreTrapped > 0，且 > 20（超出純材料差異）
        var boardFree    = new Board(FenPartialBlock);
        var boardTrapped = new Board(FenFullBlock);

        int scoreFree    = evaluator.Evaluate(boardFree);
        int scoreTrapped = evaluator.Evaluate(boardTrapped);

        int diff = scoreFree - scoreTrapped;
        // diff 應 > 20（純材料+腳位差異），代表被困懲罰確實生效
        Assert.True(diff > 20,
            $"分數差 ({diff}) 應 > 20，確認被困懲罰（-50）超越兵材料（+30）和腳封差值（-10）");
    }
}
