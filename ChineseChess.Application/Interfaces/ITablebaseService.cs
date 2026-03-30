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

    // ── 狀態（延伸）────────────────────────────────────────────────────

    /// <summary>
    /// 是否擁有完整的 Board 物件索引（生成後才有；匯入後為 false）。
    /// 僅在 <see cref="HasBoardData"/> 為 true 時才能呼叫 <see cref="SyncToTranspositionTable"/>。
    /// </summary>
    bool HasBoardData { get; }

    // ── 生成 ────────────────────────────────────────────────────────────

    /// <summary>非同步生成指定子力組合的殘局庫（倒推分析）。</summary>
    Task GenerateAsync(
        PieceConfiguration config,
        IProgress<TablebaseGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 分析棋盤上的實際子力，自動建立子力組合並非同步生成殘局庫。
    /// 適合「從當前局面生成」的工作流程。
    /// </summary>
    Task GenerateFromBoardAsync(
        IBoard board,
        IProgress<TablebaseGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>清除目前已載入的殘局庫。</summary>
    void Clear();

    // ── 查詢 ────────────────────────────────────────────────────────────

    /// <summary>查詢指定局面的殘局庫結論（僅使用 ZobristKey）。</summary>
    TablebaseEntry Query(IBoard board);

    /// <summary>
    /// 在殘局庫中找出最優著法（勝方選最快勝，敗方選最慢負）。
    /// 未找到最優著法或局面為和棋時回傳 null。
    /// </summary>
    /// <remarks>
    /// 此方法內部會對 <paramref name="board"/> 執行 MakeMove / UnmakeMove 試走還原，
    /// 呼叫端必須傳入可安全修改的棋盤物件（通常為 board.Clone() 的結果）。
    /// 若在執行過程中拋出例外，棋盤狀態可能損毀，呼叫端應自行處理。
    /// </remarks>
    Move? GetBestMove(IBoard board);

    // ── 匯出／匯入 ──────────────────────────────────────────────────────

    /// <summary>將殘局庫結論匯出為 FEN 文字檔（一行一局面）。</summary>
    Task ExportToFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>從 FEN 文字檔匯入殘局庫結論，回傳成功載入的局面數。</summary>
    Task<int> ImportFromFileAsync(string filePath, CancellationToken cancellationToken = default);

    // ── TT 同步 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 將殘局庫中所有必勝/必負局面的結論與最佳著法批次寫入指定引擎的 TT，
    /// 使 AI 搜尋時可直接命中正確步法。和棋局面不寫入（score=0 無意義）。
    /// 前置條件：
    ///   1. <see cref="HasBoardData"/> 必須為 true（即 <see cref="GenerateAsync"/> 或
    ///      <see cref="GenerateFromBoardAsync"/> 已完成，而非從檔案匯入）。
    ///   2. <see cref="IsGenerating"/> 必須為 false（不可在生成進行中同時同步）。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///   當 <see cref="HasBoardData"/> 為 false 或 <see cref="IsGenerating"/> 為 true 時拋出。
    /// </exception>
    void SyncToTranspositionTable(IAiEngine engine);
}
