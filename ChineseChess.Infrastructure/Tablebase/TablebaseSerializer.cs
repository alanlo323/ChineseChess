using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Models;
using System.IO;

namespace ChineseChess.Infrastructure.Tablebase;

/// <summary>
/// 殘局庫 FEN 序列化工具。
///
/// 檔案格式：
///   # 殘局庫：[DisplayName]（生成於 yyyy-MM-dd HH:mm）
///   [FEN] W [depth]   → 必勝（W = Win）
///   [FEN] L [depth]   → 必負（L = Loss）
///   [FEN] D 0         → 和棋（D = Draw）
/// </summary>
public static class TablebaseSerializer
{
    private const string ResultWin  = "W";
    private const string ResultLoss = "L";
    private const string ResultDraw = "D";

    /// <summary>匯入時單一檔案最大行數（防止 OOM）。</summary>
    private const int MaxImportLines = 20_000_000;

    /// <summary>匯入時單一檔案最大位元組大小（防止 OOM）：200 MB。</summary>
    private const long MaxImportBytes = 200L * 1024 * 1024;

    // ── 路徑驗證 ────────────────────────────────────────────────────────

    /// <summary>
    /// 正規化路徑，消除相對路徑（../）遍歷風險。
    /// </summary>
    private static string ValidatePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("檔案路徑不可為空。", nameof(filePath));
        return Path.GetFullPath(filePath);
    }

    // ── 匯出 ────────────────────────────────────────────────────────────

    public static async Task ExportAsync(
        TablebaseStorage storage,
        PieceConfiguration config,
        string filePath,
        CancellationToken ct = default)
    {
        var safePath = ValidatePath(filePath);
        using var writer = new StreamWriter(safePath, append: false, System.Text.Encoding.UTF8);

        await writer.WriteLineAsync(
            $"# 殘局庫：{config.DisplayName}（生成於 {DateTime.Now:yyyy-MM-dd HH:mm}）");
        await writer.WriteLineAsync(
            $"# 共 {storage.TotalPositions:N0} 局面，勝 {storage.WinCount:N0}，負 {storage.LossCount:N0}，和 {storage.DrawCount:N0}");

        foreach (var (hash, entry) in storage.GetAllEntries())
        {
            ct.ThrowIfCancellationRequested();

            // 以 Zobrist hash 作為識別符（供同一引擎版本重新匯入）
            var resultChar = ToResultChar(entry.Result);
            await writer.WriteLineAsync($"{hash} {resultChar} {entry.Depth}");
        }
    }

    /// <summary>
    /// 匯出含完整 FEN 字串的版本（需提供 Board 對應表）。
    /// </summary>
    public static async Task ExportWithFenAsync(
        TablebaseStorage storage,
        IReadOnlyDictionary<ulong, Board> boards,
        PieceConfiguration config,
        string filePath,
        CancellationToken ct = default)
    {
        var safePath = ValidatePath(filePath);
        using var writer = new StreamWriter(safePath, append: false, System.Text.Encoding.UTF8);

        await writer.WriteLineAsync(
            $"# 殘局庫：{config.DisplayName}（生成於 {DateTime.Now:yyyy-MM-dd HH:mm}）");
        await writer.WriteLineAsync(
            $"# 共 {storage.TotalPositions:N0} 局面，勝 {storage.WinCount:N0}，負 {storage.LossCount:N0}，和 {storage.DrawCount:N0}");
        await writer.WriteLineAsync("# 格式：FEN W/L/D 步數");

        foreach (var (hash, entry) in storage.GetAllEntries())
        {
            ct.ThrowIfCancellationRequested();

            if (!boards.TryGetValue(hash, out var board)) continue;

            var fen = board.ToFen();
            var resultChar = ToResultChar(entry.Result);
            await writer.WriteLineAsync($"{fen} {resultChar} {entry.Depth}");
        }
    }

    // ── 匯入 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 從檔案匯入殘局庫結論，回傳成功載入的局面數。
    /// 支援兩種格式：
    ///   1. hash W/L/D depth（由 ExportAsync 生成）
    ///   2. FEN W/L/D depth（由 ExportWithFenAsync 生成）
    ///
    /// 安全保護：
    ///   - 路徑正規化（防路徑遍歷）
    ///   - 檔案大小上限（防 OOM）
    ///   - 行數上限（防 OOM）
    ///   - 逐行讀取（StreamReader，非整批載入）
    /// </summary>
    public static async Task<int> ImportAsync(
        TablebaseStorage storage,
        string filePath,
        CancellationToken ct = default)
    {
        var safePath = ValidatePath(filePath);

        // 檢查檔案大小上限
        var info = new FileInfo(safePath);
        if (!info.Exists)
            throw new FileNotFoundException($"找不到殘局庫檔案：{safePath}");
        if (info.Length > MaxImportBytes)
            throw new InvalidOperationException(
                $"殘局庫檔案過大（{info.Length / 1024 / 1024:N0} MB），超過 {MaxImportBytes / 1024 / 1024} MB 上限。");

        int count = 0;
        int lineCount = 0;

        using var reader = new StreamReader(safePath, System.Text.Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (++lineCount > MaxImportLines)
                throw new InvalidOperationException(
                    $"殘局庫檔案行數超過 {MaxImportLines:N0} 行上限。");

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            // 最後兩個 token 是 result + depth
            var resultStr = parts[^2];
            if (!int.TryParse(parts[^1], out int depth)) continue;

            var result = ParseResult(resultStr);
            if (result is null) continue;

            // 判斷是 hash 格式（純數字）還是 FEN 格式（含 '/'）
            if (!parts[0].Contains('/') && ulong.TryParse(parts[0], out ulong hash))
            {
                storage.Store(hash, new Domain.Models.TablebaseEntry(result.Value, depth));
                count++;
            }
            else
            {
                // FEN 格式：parts[0..^2] 合併為完整 FEN 字串
                var fen = string.Join(' ', parts[..^2]);
                try
                {
                    var board = new Board(fen);
                    storage.Store(board.ZobristKey, new Domain.Models.TablebaseEntry(result.Value, depth));
                    count++;
                }
                catch
                {
                    // 無效 FEN，跳過此行
                }
            }
        }

        return count;
    }

    // ── 私有輔助 ────────────────────────────────────────────────────────

    private static string ToResultChar(TablebaseResult result) => result switch
    {
        TablebaseResult.Win  => ResultWin,
        TablebaseResult.Loss => ResultLoss,
        _                    => ResultDraw,
    };

    private static TablebaseResult? ParseResult(string s) => s switch
    {
        ResultWin  => TablebaseResult.Win,
        ResultLoss => TablebaseResult.Loss,
        ResultDraw => TablebaseResult.Draw,
        _          => null,
    };
}
