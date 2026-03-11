using ChineseChess.Domain.Entities;
using System.Linq;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// 驗證 GenerateLegalMoves 的合法性過濾行為。
/// 這些測試用於確保「增量式 IsCheck 快速路徑」優化前後行為完全等價。
///
/// 棋盤索引：index = row * 9 + col（row 0 = 黑方底線，row 9 = 紅方底線）
///
/// 核心邏輯：若走法的 from 與 to 都不在將帥的同行/列上，
/// 則不可能暴露將帥（可跳過完整 IsCheck）。
/// </summary>
public class LegalMovesOptimizationTests
{
    // ─── 被釘住的棋子（同列有敵車）────────────────────────────────────────

    [Fact]
    public void PinnedRook_OnKingColumn_CannotMoveOffColumn()
    {
        // 局面：黑車(0,4)=4  紅車(5,4)=49  紅帥(9,4)=85  黑將(0,0)=0
        // 紅車在帥的同列，被黑車釘住，只能沿第 4 列移動
        var board = new Board("k3r4/9/9/9/9/4R4/9/9/9/4K4 w - - 0 1");
        var rookMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();

        // 紅車只能沿 col=4 移動：往上到 40,31,22,13,4（吃黑車）；往下到 58,67,76
        Assert.All(rookMoves, m =>
        {
            int toCol = m.To % 9;
            Assert.Equal(4, toCol); // 只允許第 4 列的目標
        });

        // 應包含往上的所有合法格：40, 31, 22, 13（移至空格）以及 4（吃子）
        Assert.Contains(new Move(49, 40), rookMoves);
        Assert.Contains(new Move(49, 31), rookMoves);
        Assert.Contains(new Move(49, 22), rookMoves);
        Assert.Contains(new Move(49, 13), rookMoves);
        Assert.Contains(new Move(49, 4),  rookMoves); // 吃掉黑車

        // 應包含往下的合法格（朝帥方向，但不含帥本身）：58, 67, 76
        Assert.Contains(new Move(49, 58), rookMoves);
        Assert.Contains(new Move(49, 67), rookMoves);
        Assert.Contains(new Move(49, 76), rookMoves);

        // 不應包含任何橫向移動（離開 col=4）
        Assert.DoesNotContain(rookMoves, m => m.To % 9 != 4);
    }

    [Fact]
    public void PinnedRook_OnKingColumn_TotalMoveCount_IsCorrect()
    {
        // 同上局面，驗證整體合法著法數（紅車 8 步 + 紅帥 3 步 = 11）
        var board = new Board("k3r4/9/9/9/9/4R4/9/9/9/4K4 w - - 0 1");
        var allMoves = board.GenerateLegalMoves().ToList();
        Assert.Equal(11, allMoves.Count);
    }

    // ─── 被炮釘住（炮需要炮台，移走炮台後炮失去攻擊能力，但我方棋子仍是炮台時才形成釘住）

    [Fact]
    public void RookActingAsCannonScreen_WhenInCheck_CanCaptureOrMoveHorizontally()
    {
        // 局面：黑將(0,0)=0  黑炮(0,4)=4  紅車(5,4)=49（炮台）  紅帥(9,4)=85
        // 黑炮透過紅車（炮台）攻擊紅帥 → 紅方在將軍中
        var board = new Board("k3c4/9/9/9/9/4R4/9/9/9/4K4 w - - 0 1");

        var rookMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 49)
            .ToList();

        // 解將方式 1：吃掉黑炮（炮消失，無法將軍）
        Assert.Contains(new Move(49, 4), rookMoves);

        // 解將方式 2：紅車橫移離開 col=4
        // 炮需要「恰好一個」炮台才能射擊；紅車離列後炮無炮台，無法攻帥
        Assert.Contains(new Move(49, 45), rookMoves); // 橫移至 (5,0)
        Assert.Contains(new Move(49, 46), rookMoves); // 橫移至 (5,1)
        Assert.Contains(new Move(49, 50), rookMoves); // 橫移至 (5,5)
        Assert.Contains(new Move(49, 53), rookMoves); // 橫移至 (5,8)

        // 縱移但不吃炮：紅車仍在 col=4，仍是炮台，帥仍在將軍中 → 不合法
        Assert.DoesNotContain(new Move(49, 40), rookMoves); // 上移至 (4,4)
        Assert.DoesNotContain(new Move(49, 58), rookMoves); // 下移至 (6,4)

        // 合計：吃炮 1 步 + 橫移 8 步 = 9 步
        Assert.Equal(9, rookMoves.Count);
    }

    // ─── 不在將帥行/列上的棋子可自由移動 ─────────────────────────────────

    [Fact]
    public void FreeHorse_NotOnKingLine_HasAllExpectedMoves()
    {
        // 局面：黑將(0,0)=0  紅馬(8,2)=74  紅帥(9,4)=85
        // 紅馬不在帥的同行（row=9）或同列（col=4），可自由移動
        var board = new Board("k8/9/9/9/9/9/9/9/2N6/4K4 w - - 0 1");
        var horseMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 74)
            .ToList();

        // 馬在 (8,2) 的可達格（排除出界和同色棋子）：
        // (-2,+1)=(6,3)=57, (-2,-1)=(6,1)=55, (+1,-2)=(9,0)=81, (-1,+2)=(7,4)=67, (-1,-2)=(7,0)=63
        // (+1,+2)=(9,4)=85 → 紅帥，不可吃 → 排除
        // (+2,±1)=(10,..） → 出界 → 排除
        Assert.Contains(new Move(74, 57), horseMoves); // 上2右1
        Assert.Contains(new Move(74, 55), horseMoves); // 上2左1
        Assert.Contains(new Move(74, 81), horseMoves); // 下1左2
        Assert.Contains(new Move(74, 67), horseMoves); // 上1右2
        Assert.Contains(new Move(74, 63), horseMoves); // 上1左2
        Assert.Equal(5, horseMoves.Count);
    }

    // ─── 等價性驗證：多種局面下移動集合完全一致 ─────────────────────────

    [Theory]
    [InlineData("k8/9/9/9/9/9/9/3A5/9/4K4 w - - 0 1")]          // 仕在帥列上
    [InlineData("k8/9/9/9/9/9/9/9/9/3AK4 w - - 0 1")]            // 仕在帥行上
    [InlineData("k8/9/9/9/9/3R5/9/9/9/4K4 w - - 0 1")]           // 車在帥列上（未被釘）
    [InlineData("k3r4/9/9/9/9/4R4/9/9/9/4K4 w - - 0 1")]         // 車被敵車釘住
    [InlineData("k8/9/9/9/9/9/9/9/2N6/4K4 w - - 0 1")]           // 馬不在帥行列
    [InlineData("k8/9/9/9/9/9/2r6/9/2R6/4K4 w - - 0 1")]         // 車被敵車釘在同列
    [InlineData("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1")] // 開局
    public void GenerateLegalMoves_ProducesSameMoveSet_AsReferenceImplementation(string fen)
    {
        // 驗證優化後的 GenerateLegalMoves 與暴力版（全部驗證）產生相同的移動集合
        // 這個測試確保重構不改變行為
        var board = new Board(fen);
        var optimizedMoves = board.GenerateLegalMoves()
            .OrderBy(m => m.From).ThenBy(m => m.To)
            .ToList();

        // 暴力驗證版本（直接 MakeMove/IsCheck/UnmakeMove）
        var referenceMoves = board.GeneratePseudoLegalMoves()
            .Where(move =>
            {
                var color = board.Turn;
                board.MakeMove(move);
                bool isLegal = !board.IsCheck(color);
                board.UnmakeMove(move);
                return isLegal;
            })
            .OrderBy(m => m.From).ThenBy(m => m.To)
            .ToList();

        Assert.Equal(referenceMoves.Count, optimizedMoves.Count);
        for (int i = 0; i < referenceMoves.Count; i++)
        {
            Assert.Equal(referenceMoves[i], optimizedMoves[i]);
        }
    }

    // ─── 將帥自身移動的安全性 ──────────────────────────────────────────────

    [Fact]
    public void King_CannotMoveIntoCheck()
    {
        // 局面：黑車(0,4)=4  紅帥(9,4)=85  黑將(0,0)=0
        // 帥在 col=4，被黑車控制，帥只能橫移（不能往上進入 col=4 的攻擊線）
        // 帥的合法移動：(9,3)=84, (9,5)=86（橫移）
        // 帥往 (8,4)=76 → 仍在 col=4，被黑車將 → 不合法
        var board = new Board("k3r4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        var kingMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 85)
            .ToList();

        Assert.DoesNotContain(new Move(85, 76), kingMoves); // 不能走進被車控制的格
        Assert.Contains(new Move(85, 84), kingMoves);       // 橫移可以
        Assert.Contains(new Move(85, 86), kingMoves);       // 橫移可以
        Assert.Equal(2, kingMoves.Count);
    }

    [Fact]
    public void King_FlyingGeneral_CannotMoveToFaceOpponent()
    {
        // 局面：黑將(0,4)=4  紅帥(9,4)=85（面將狀態，帥不能橫移讓面將成立但面將已成立？）
        // 實際上，面將已成立（已在將軍狀態）→ 帥必須逃離 col=4，或有子阻擋
        // 帥的合法移動：只有 (9,3)=84 和 (9,5)=86（橫移出 col=4）
        // 帥不能走 (8,4)=76：仍在 col=4 且面將
        var board = new Board("4k4/9/9/9/9/9/9/9/9/4K4 w - - 0 1");
        var kingMoves = board.GenerateLegalMoves()
            .Where(m => m.From == 85)
            .ToList();

        Assert.DoesNotContain(new Move(85, 76), kingMoves); // 仍在 col=4，面將
        Assert.Contains(new Move(85, 84), kingMoves);
        Assert.Contains(new Move(85, 86), kingMoves);
        Assert.Equal(2, kingMoves.Count);
    }
}
