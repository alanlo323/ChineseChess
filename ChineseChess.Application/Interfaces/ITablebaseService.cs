using ChineseChess.Application.Models;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Models;

namespace ChineseChess.Application.Interfaces;

/// <summary>殘局庫服務合約。</summary>
public interface ITablebaseService
{
    // ── 狀態 ────────────────────────────────────────────────────────────

    bool HasTablebase { get; }
    PieceConfiguration? CurrentConfiguration { get; }
    bool IsGenerating { get; }

    int TotalPositions  { get; }
    int WinPositions    { get; }
    int LossPositions   { get; }
    int DrawPositions   { get; }

    // ── 生成 ────────────────────────────────────────────────────────────

    /// <summary>非同步生成指定子力組合的殘局庫（倒推分析）。</summary>
    Task GenerateAsync(
        PieceConfiguration config,
        IProgress<TablebaseGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>清除目前已載入的殘局庫。</summary>
    void Clear();

    // ── 查詢 ────────────────────────────────────────────────────────────

    /// <summary>查詢指定局面的殘局庫結論（僅使用 ZobristKey）。</summary>
    TablebaseEntry Query(IBoard board);

    /// <summary>在已生成的殘局庫中找出最優著法（勝方選最快勝，敗方選最慢負）。</summary>
    Move? GetBestMove(Board board);

    // ── 匯出／匯入 ──────────────────────────────────────────────────────

    /// <summary>將殘局庫結論匯出為 FEN 文字檔（一行一局面）。</summary>
    Task ExportToFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>從 FEN 文字檔匯入殘局庫結論，回傳成功載入的局面數。</summary>
    Task<int> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default);
}
