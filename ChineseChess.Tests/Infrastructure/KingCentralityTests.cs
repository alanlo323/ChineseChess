using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Evaluators;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// 驗證殘局帥/將趨中宮評估（B1）。
/// 殘局時帥/將靠近九宮中心應獲得加分，反映指揮作戰的優勢。
/// 九宮中心：紅方 (row=8, col=4)；黑方 (row=1, col=4)。
/// </summary>
public class KingCentralityTests
{
    private readonly HandcraftedEvaluator evaluator = new();

    // 殘局局面（僅剩雙方帥/將），phase≈0
    // 紅帥在九宮中心 (8,4)，與對方將不在同一縱列（無飛將）
    private const string FenKingCenter = "3k5/9/9/9/9/9/9/9/4K4/9 w - - 0 1";

    // 紅帥在九宮角落 (7,3)，與對方將不在同一縱列
    private const string FenKingCorner = "5k3/9/9/9/9/9/9/3K5/9/9 w - - 0 1";

    [Fact]
    public void KingCentrality_CenterPalace_ScoresHigherThanCorner()
    {
        // 殘局（phase≈0）中，帥在九宮中心 (8,4) 應比角落 (7,3) 得分更高
        // 預期差值：PST(4→2) + 趨中宮獎勵(20-6=14) ≈ +16
        var boardCenter = new Board(FenKingCenter);
        var boardCorner = new Board(FenKingCorner);

        int scoreCenter = evaluator.Evaluate(boardCenter);
        int scoreCorner = evaluator.Evaluate(boardCorner);

        Assert.True(scoreCenter > scoreCorner,
            $"九宮中心帥 ({scoreCenter}) 應比角落帥 ({scoreCorner}) 得分更高（殘局趨中宮）");
        int diff = scoreCenter - scoreCorner;
        Assert.True(diff > 5,
            $"分數差 ({diff}) 應 > 5，確認殘局趨中宮獎勵（≈+8）遠超過 PST 差值（≈+2），沒有 B1 功能時此值僅約 2~3");
    }

    [Fact]
    public void KingCentrality_MiddlePalace_ScoresBetweenCenterAndCorner()
    {
        // 紅帥在 (8,3)（中宮左側，距中心距離=1）
        // 分數應介於中心與角落之間
        // FenKingMid: 帥在 (8,3)，對方將在 (0,5)（不同縱列）
        const string fenKingMid = "5k3/9/9/9/9/9/9/9/3K5/9 w - - 0 1";
        var boardCenter = new Board(FenKingCenter);
        var boardMid    = new Board(fenKingMid);
        var boardCorner = new Board(FenKingCorner);

        int scoreCenter = evaluator.Evaluate(boardCenter);
        int scoreMid    = evaluator.Evaluate(boardMid);
        int scoreCorner = evaluator.Evaluate(boardCorner);

        Assert.True(scoreCenter >= scoreMid,
            $"中心帥 ({scoreCenter}) 應 >= 側中帥 ({scoreMid})");
        Assert.True(scoreMid >= scoreCorner,
            $"側中帥 ({scoreMid}) 應 >= 角落帥 ({scoreCorner})");
    }
}
