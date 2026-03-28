using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Models;
using ChineseChess.Infrastructure.AI.Search;
using ChineseChess.Infrastructure.Tablebase;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>殘局庫功能改進測試（Feature A: 從棋盤生成，Feature B: 同步至TT）。</summary>
public class EtbImprovementsTests
{
    // ── 工具方法 ──────────────────────────────────────────────────────────

    /// <summary>建立只有帥/將與一枚紅方額外棋子的空棋盤。</summary>
    private static Board BuildBoard(PieceType redExtra, int redExtraIndex = 81)
    {
        var board = new Board();
        // 紅帥在九宮中心 (row=9, col=4 → index=85)
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));
        // 黑將在九宮中心 (row=0, col=4 → index=4)
        board.SetPiece(4, new Piece(PieceColor.Black, PieceType.King));
        board.SetPiece(redExtraIndex, new Piece(PieceColor.Red, redExtra));
        board.SetTurn(PieceColor.Red);
        return board;
    }

    /// <summary>建立只有帥/將的空棋盤。</summary>
    private static Board BuildKingsOnlyBoard()
    {
        var board = new Board();
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));
        board.SetPiece(4, new Piece(PieceColor.Black, PieceType.King));
        board.SetTurn(PieceColor.Red);
        return board;
    }

    // ════════════════════════════════════════════════════════════════════
    // Feature A：GenerateFromBoardAsync
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GenerateFromBoard_RookPosition_ProducesTablebase()
    {
        // 棋盤上有帥、車、將 → 等同於 RookVsKing 子力組合
        var board = BuildBoard(PieceType.Rook);
        var service = new TablebaseService();

        await service.GenerateFromBoardAsync(board);

        Assert.True(service.HasTablebase, "應成功生成殘局庫");
        Assert.True(service.WinPositions > 0, "帥車 vs 將 應有必勝局面");
        Assert.True(service.LossPositions > 0, "帥車 vs 將 應有必負局面");
    }

    [Fact]
    public async Task GenerateFromBoard_KingsOnly_ProducesDrawOnlyTablebase()
    {
        // 棋盤上只有帥、將 → 等同於 KingsOnly
        var board = BuildKingsOnlyBoard();
        var service = new TablebaseService();

        await service.GenerateFromBoardAsync(board);

        Assert.True(service.HasTablebase);
        Assert.Equal(0, service.WinPositions);
        Assert.Equal(0, service.LossPositions);
    }

    [Fact]
    public async Task GenerateFromBoard_ResultMatchesEquivalentPreset()
    {
        // 從棋盤生成的結果應與直接使用預設組合的結果一致
        var board = BuildBoard(PieceType.Rook);
        var serviceFromBoard = new TablebaseService();
        var serviceFromPreset = new TablebaseService();

        await serviceFromBoard.GenerateFromBoardAsync(board);
        await serviceFromPreset.GenerateAsync(PieceConfiguration.RookVsKing);

        Assert.Equal(serviceFromPreset.TotalPositions, serviceFromBoard.TotalPositions);
        Assert.Equal(serviceFromPreset.WinPositions,   serviceFromBoard.WinPositions);
        Assert.Equal(serviceFromPreset.LossPositions,  serviceFromBoard.LossPositions);
    }

    [Fact]
    public async Task GenerateFromBoard_SetsCurrentConfiguration()
    {
        // 生成後應能查詢 CurrentConfiguration
        var board = BuildBoard(PieceType.Rook);
        var service = new TablebaseService();

        await service.GenerateFromBoardAsync(board);

        Assert.NotNull(service.CurrentConfiguration);
        Assert.Single(service.CurrentConfiguration!.RedExtra);
        Assert.Equal(PieceType.Rook, service.CurrentConfiguration.RedExtra[0]);
        Assert.Empty(service.CurrentConfiguration.BlackExtra);
    }

    [Fact]
    public async Task GenerateFromBoard_MissingKing_ThrowsArgumentException()
    {
        // 棋盤上只有紅帥，沒有黑將 → 應拋出 ArgumentException
        var board = new Board();
        board.SetPiece(85, new Piece(PieceColor.Red, PieceType.King));
        board.SetPiece(81, new Piece(PieceColor.Red, PieceType.Rook));
        var service = new TablebaseService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateFromBoardAsync(board));
    }

    // ════════════════════════════════════════════════════════════════════
    // Feature B：HasBoardData
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task HasBoardData_AfterGenerate_ReturnsTrue()
    {
        var service = new TablebaseService();
        await service.GenerateAsync(PieceConfiguration.KingsOnly);

        Assert.True(service.HasBoardData, "生成後 boardIndex 應有資料");
    }

    [Fact]
    public void HasBoardData_BeforeGenerate_ReturnsFalse()
    {
        var service = new TablebaseService();

        Assert.False(service.HasBoardData, "初始狀態 boardIndex 應為空");
    }

    [Fact]
    public async Task HasBoardData_AfterImport_ReturnsFalse()
    {
        // Import 只載入 hash→結論，不重建 Board 物件
        var service = new TablebaseService();
        await service.GenerateAsync(PieceConfiguration.KingsOnly);

        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            await service.ExportToFileAsync(tempFile);
            var importedService = new TablebaseService();
            await importedService.ImportFromFileAsync(tempFile);

            Assert.True(importedService.HasTablebase, "匯入後應有殘局庫資料");
            Assert.False(importedService.HasBoardData, "匯入後 boardIndex 應為空");
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Feature B：SyncToTranspositionTable
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncToTT_AfterGenerate_PopulatesEntries()
    {
        var service = new TablebaseService();
        var engine  = new SearchEngine();

        await service.GenerateAsync(PieceConfiguration.RookVsKing);
        service.SyncToTranspositionTable(engine);

        var entries = engine.EnumerateTTEntries().ToList();
        Assert.True(entries.Count > 0, "同步後 TT 應有條目");
    }

    [Fact]
    public async Task SyncToTT_WinEntries_HavePositiveScore()
    {
        var service = new TablebaseService();
        var engine  = new SearchEngine();

        await service.GenerateAsync(PieceConfiguration.RookVsKing);
        service.SyncToTranspositionTable(engine);

        // 所有 TT 條目（Win/Loss）分數應為非零且符號正確
        var entries = engine.EnumerateTTEntries().ToList();
        Assert.True(entries.Any(e => e.Score > 0),  "應有正分（必勝）條目");
        Assert.True(entries.Any(e => e.Score < 0),  "應有負分（必負）條目");
        Assert.DoesNotContain(entries, e => e.Score == 0); // Draw 不寫入
    }

    [Fact]
    public async Task SyncToTT_ScoreEncoding_WinIsHighPositive()
    {
        // Win 深度越淺（越快勝），分數越高
        const int MateScore = 20000;
        var service = new TablebaseService();
        var engine  = new SearchEngine();

        await service.GenerateAsync(PieceConfiguration.RookVsKing);
        service.SyncToTranspositionTable(engine);

        var entries = engine.EnumerateTTEntries().ToList();
        // 帥車 vs 將 中必有快速將死局面，最高分應接近 MateScore
        var maxScore = entries.Max(e => e.Score);
        Assert.True(maxScore >= MateScore - 2,
            $"最高分 {maxScore} 應接近 {MateScore}（帥車 vs 將 最少 1 步將死，即 Win(depth≤2)）");
    }

    [Fact]
    public async Task SyncToTT_DrawPositions_NotStoredInTT()
    {
        // KingsOnly 全為和棋，同步後 TT 應空
        var service = new TablebaseService();
        var engine  = new SearchEngine();

        await service.GenerateAsync(PieceConfiguration.KingsOnly);
        service.SyncToTranspositionTable(engine);

        var entries = engine.EnumerateTTEntries().ToList();
        Assert.Empty(entries);
    }

    [Fact]
    public void SyncToTT_WithoutBoardData_ThrowsInvalidOperation()
    {
        // 未生成（或只匯入）時呼叫同步應拋出 InvalidOperationException
        var service = new TablebaseService();
        var engine  = new SearchEngine();

        Assert.Throws<InvalidOperationException>(() => service.SyncToTranspositionTable(engine));
    }
}
