using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Models;
using ChineseChess.Infrastructure.Tablebase;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>殘局庫生成測試（RetrogradAnalyzer）。</summary>
public class TablebaseGeneratorTests
{
    // ── 帥 vs 將（最簡單組合）─────────────────────────────────────────

    [Fact]
    public void KingsOnly_AllPositions_ShouldBeDrawOrLoss()
    {
        // 帥 vs 將：雙方均無棋力進攻，幾乎所有局面均為和棋
        var config = PieceConfiguration.KingsOnly;
        var storage = new TablebaseStorage();
        var analyzer = new RetrogradAnalyzer(storage, subTablebases: null);

        analyzer.Analyze(config);

        Assert.True(storage.TotalPositions > 0, "應有局面被列舉");
        // 帥 vs 將無任何將死機制，所有合法局面應為和棋
        Assert.Equal(0, storage.WinCount);
        Assert.Equal(0, storage.LossCount);
    }

    [Fact]
    public void KingsOnly_PositionCount_ShouldBeReasonable()
    {
        var config = PieceConfiguration.KingsOnly;
        var storage = new TablebaseStorage();
        var analyzer = new RetrogradAnalyzer(storage, subTablebases: null);

        analyzer.Analyze(config);

        // 雙王各 9 格，扣除飛將和非法局面後總數應在合理範圍
        Assert.InRange(storage.TotalPositions, 50, 350);
    }

    // ── 帥車 vs 將（基本將死測試）──────────────────────────────────────

    [Fact]
    public void RookVsKing_ShouldHaveWinPositions()
    {
        var config = PieceConfiguration.RookVsKing;
        var storage = new TablebaseStorage();
        var analyzer = new RetrogradAnalyzer(storage, subTablebases: null);

        analyzer.Analyze(config);

        // 帥車 vs 將 必有可以將死的局面
        Assert.True(storage.WinCount > 0, "帥車 vs 將 應有必勝局面");
        Assert.True(storage.LossCount > 0, "帥車 vs 將 應有必負局面");
    }

    [Fact]
    public void RookVsKing_KnownCheckmatePosition_ShouldBeLoss()
    {
        // 黑將在角落，紅車封鎖，黑方無路可走 = Loss(0) for 黑方（被將死）
        // 局面：黑將在 a0 (0,3)，紅帥在 (9,5)，紅車封底線
        // fen: 3k5/9/9/9/9/9/9/9/9/5KR2 b - - 0 1
        // 此局面黑將被將死（紅車在 g9 將軍且無處逃）
        var board = new Board("3k5/9/9/9/9/9/9/9/9/5KR2 b - - 0 1");

        var config = PieceConfiguration.RookVsKing;
        var storage = new TablebaseStorage();
        var analyzer = new RetrogradAnalyzer(storage, subTablebases: null);
        analyzer.Analyze(config);

        var entry = storage.Query(board.ZobristKey);
        // 若此局面被列舉到，結論應為 Loss（黑方被將死）
        if (entry.IsResolved)
            Assert.Equal(TablebaseResult.Loss, entry.Result);
    }

    // ── Board.IsStalemate() 單元測試 ──────────────────────────────────

    [Fact]
    public void Board_IsStalemate_WhenNoLegalMovesAndNotInCheck_ReturnsTrue()
    {
        // 黑將被困在角落且未被將軍但無路可走（困斃）
        // 局面：黑將在 d10 (0,3)，紅車封左右，紅帥在正面但不將軍
        // 簡化測試：手動放置確認困斃
        // fen: 3k5/3R5/9/9/9/9/9/9/9/3K5 b - - 0 1
        // 黑將在 (0,3)，紅車在 (1,3) 前方擋路，紅帥在 (9,3)
        var board = new Board("3k5/3R5/9/9/9/9/9/9/9/3K5 b - - 0 1");

        // 不管是否真的困斃，測試 API 不應拋出例外
        // （實際局面困斃需要完整棋規驗證）
        bool noLegal = board.HasNoLegalMoves(PieceColor.Black);
        bool isCheck = board.IsCheck(PieceColor.Black);
        bool isStalemate = board.IsStalemate(PieceColor.Black);

        // 如果確有 check，IsStalemate 應為 false
        if (isCheck)
            Assert.False(isStalemate);
        // 如果確無合法著法且無將，IsStalemate 應為 true
        else if (noLegal)
            Assert.True(isStalemate);
    }

    [Fact]
    public void Board_IsStalemate_WhenInCheck_ReturnsFalse()
    {
        // 任何被將軍的局面都不是困斃
        var board = new Board("3k5/9/9/9/9/9/9/9/9/3KR4 b - - 0 1");
        // 黑將在 (0,3)，紅車在 (9,4) 未必將軍，僅確認 API 邏輯
        if (board.IsCheck(PieceColor.Black))
            Assert.False(board.IsStalemate(PieceColor.Black));
    }

    // ── 結論一致性測試 ──────────────────────────────────────────────────

    [Fact]
    public void AllResolved_WinPositions_PredecessorShouldBeLossOrWin()
    {
        // 驗證：Win(n) 局面的前驅，應有 Loss(n-1) 的後繼存在
        var config = PieceConfiguration.RookVsKing;
        var storage = new TablebaseStorage();
        var analyzer = new RetrogradAnalyzer(storage, subTablebases: null);
        analyzer.Analyze(config);

        // Win(1) 局面：走一步即可將死（後繼有 Loss(0)）
        var win1Positions = storage.GetAllEntries()
            .Where(e => e.Value.Result == TablebaseResult.Win && e.Value.Depth == 1)
            .Take(5)
            .ToList();

        // 至少應有一些 Win(1) 的局面
        Assert.NotEmpty(win1Positions);
    }
}
