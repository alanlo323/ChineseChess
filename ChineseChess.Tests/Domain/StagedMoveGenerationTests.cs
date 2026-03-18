using ChineseChess.Domain.Entities;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ChineseChess.Tests.Domain;

/// <summary>
/// 驗證 Staged Move Generation（分段著法產生）的正確性。
///
/// 核心不變式：GenerateCaptureMoves() ∪ GenerateQuietMoves() == GenerateLegalMoves()
///
/// 棋盤索引：index = row * 9 + col（row 0 = 黑方底線，row 9 = 紅方底線）
/// </summary>
public class StagedMoveGenerationTests
{
    // 標準開局 FEN
    private const string InitialFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // 有吃子機會的局面：紅車可吃黑卒
    private const string CaptureFen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // 被釘住局面：紅車被黑車釘在帥的同列上
    private const string PinnedPieceFen = "k3r4/9/9/9/9/4R4/9/9/9/4K4 w - - 0 1";

    // 將軍局面：紅帥被黑車將軍
    private const string InCheckFen = "k8/9/9/9/9/9/9/9/4r4/4K4 w - - 0 1";

    // 含吃子機會的中局
    private const string MidgameFen = "r1bakabnr/4n4/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";

    // ─── 測試 1：GenerateCaptureMoves 只回傳吃子著法 ───────────────────────

    [Fact]
    public void GenerateCaptureMoves_ReturnsOnlyCaptures()
    {
        // 使用有實際吃子的局面：紅車(85)可以往上走，目前開局無法直接吃子
        // 改用有吃子機會的局面
        var board = new Board("4k4/9/9/9/9/9/9/9/4R4/4K4 w - - 0 1");

        var captures = board.GenerateCaptureMoves().ToList();

        // 所有回傳著法的目標格均有棋子
        Assert.All(captures, m =>
        {
            var targetPiece = board.GetPiece(m.To);
            Assert.False(targetPiece.IsNone, $"著法 {m.From}->{m.To} 目標格應有棋子");
        });
    }

    // ─── 測試 2：GenerateQuietMoves 只回傳非吃子著法 ─────────────────────

    [Fact]
    public void GenerateQuietMoves_ReturnsOnlyNonCaptures()
    {
        var board = new Board(InitialFen);

        var quietMoves = board.GenerateQuietMoves().ToList();

        // 所有回傳著法的目標格均空
        Assert.All(quietMoves, m =>
        {
            var targetPiece = board.GetPiece(m.To);
            Assert.True(targetPiece.IsNone, $"著法 {m.From}->{m.To} 目標格應為空");
        });
    }

    // ─── 測試 3：Captures 是 LegalMoves 的子集 ────────────────────────────

    [Fact]
    public void GenerateCaptureMoves_SubsetOfLegalMoves()
    {
        var board = new Board(MidgameFen);

        var legal = board.GenerateLegalMoves().ToHashSet();
        var captures = board.GenerateCaptureMoves().ToList();

        Assert.All(captures, m => Assert.Contains(m, legal));
    }

    // ─── 測試 4：Quiets 是 LegalMoves 的子集 ─────────────────────────────

    [Fact]
    public void GenerateQuietMoves_SubsetOfLegalMoves()
    {
        var board = new Board(InitialFen);

        var legal = board.GenerateLegalMoves().ToHashSet();
        var quietMoves = board.GenerateQuietMoves().ToList();

        Assert.All(quietMoves, m => Assert.Contains(m, legal));
    }

    // ─── 測試 5：關鍵正確性不變式（核心測試）─────────────────────────────

    [Theory]
    [InlineData("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1")]
    [InlineData("k3r4/9/9/9/9/4R4/9/9/9/4K4 w - - 0 1")]
    [InlineData("4k4/9/9/9/9/9/9/9/4R4/4K4 w - - 0 1")]
    [InlineData("r1bakabnr/4n4/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1")]
    [InlineData("rnbakab1r/9/1c4nc1/p1p1p1p1p/9/2P6/P3P1P1P/1C4NC1/9/RNBAKAB1R w - - 0 1")]
    [InlineData("r3k1b1r/9/1cn1b1nc1/p1p3p1p/6p2/9/P1P3P1P/1CN1B1NC1/9/R3K1B1R w - - 0 1")]
    [InlineData("3ak4/9/3a5/9/9/9/9/9/9/3AK4 w - - 0 1")]
    [InlineData("k8/9/9/r8/9/9/9/9/9/K8 w - - 0 1")]
    [InlineData("4k4/9/9/9/9/9/9/9/4r4/4K4 w - - 0 1")]
    [InlineData("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR b - - 0 1")]
    public void CapturesPlusQuiets_EqualsLegalMoves(string fen)
    {
        var board = new Board(fen);

        var legal = board.GenerateLegalMoves()
            .OrderBy(m => m.From).ThenBy(m => m.To)
            .ToList();

        var captures = board.GenerateCaptureMoves().ToList();
        var quiets = board.GenerateQuietMoves().ToList();

        var union = captures.Concat(quiets)
            .OrderBy(m => m.From).ThenBy(m => m.To)
            .ToList();

        Assert.Equal(legal.Count, union.Count);
        Assert.Equal(legal, union);
    }

    // ─── 測試 6：被牽制棋子的吃子仍被合法性過濾 ─────────────────────────

    [Fact]
    public void GenerateCaptureMoves_RespectsLegality_PinnedPiece()
    {
        // 局面：黑車(0,4)=4  紅車(5,4)=49  紅帥(9,4)=85  黑將(0,0)=0
        // 紅車被釘在帥的同列，不能橫向移動（即使目標格有棋子也不行）
        var board = new Board(PinnedPieceFen);

        var rookCaptures = board.GenerateCaptureMoves()
            .Where(m => m.From == 49)
            .ToList();

        // 被釘住的車只能沿 col=4 方向吃子（吃 index=4 的黑車）
        Assert.All(rookCaptures, m =>
        {
            int toCol = m.To % 9;
            Assert.Equal(4, toCol);
        });

        // 確認可以沿列方向吃子（吃掉黑車）
        Assert.Contains(new Move(49, 4), rookCaptures);
    }

    // ─── 測試 7：將軍局面只回傳能解將的吃子著法 ──────────────────────────

    [Fact]
    public void GenerateCaptureMoves_InCheck_OnlyLegalCaptures()
    {
        // 局面：黑車(8,4)=76 直接將軍紅帥(9,4)=85，紅只能移帥或擋
        // 使用將軍局面：黑車在 col=4 直接對著紅帥
        var board = new Board(InCheckFen);

        var captures = board.GenerateCaptureMoves().ToList();

        // 所有回傳的吃子著法都必須是合法著法（能解將）
        var legal = board.GenerateLegalMoves().ToHashSet();
        Assert.All(captures, m => Assert.Contains(m, legal));

        // 確認是將軍局面（合法著法數目有限）
        Assert.True(board.IsCheck(board.Turn),
            "此局面應為將軍狀態");
    }
}
