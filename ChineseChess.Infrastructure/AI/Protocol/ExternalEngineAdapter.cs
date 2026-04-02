using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Infrastructure.AI.Protocol;

/// <summary>
/// 包裝外部 UCI / UCCI 引擎 process 的 IAiEngine 實作。
///
/// 生命週期：
///   1. 建立實例 → process 尚未啟動
///   2. 呼叫 <see cref="InitializeAsync"/> → 啟動 process，完成握手
///   3. 呼叫 <see cref="SearchAsync"/> → 送出 position + go，等待 bestmove
///   4. <see cref="Dispose"/> → 送出 quit，結束 process
///
/// TT 相關方法均為 stub（外部引擎自行管理 TT）。
/// </summary>
public sealed class ExternalEngineAdapter : IExternalEngineAdapter, IDisposable
{
    private readonly string executablePath;
    private readonly EngineProtocol protocol;

    private Process? process;
    private StreamWriter? input;
    private readonly SemaphoreSlim initLock = new SemaphoreSlim(1, 1);
    private bool initialized;
    private bool disposed;
    private string engineName = string.Empty;
    private string engineAuthor = string.Empty;
    private int? eloRating;

    /// <summary>握手後解析到的引擎名稱（如 "Pikafish 2026-01-02"）。</summary>
    public string EngineName => engineName;

    /// <summary>握手後解析到的引擎作者。</summary>
    public string EngineAuthor => engineAuthor;

    /// <summary>握手後解析到的預設 ELO（來自 UCI_Elo option 的 default 值）。</summary>
    public int? EloRating => eloRating;

    /// <summary>建構時指定的引擎協議。</summary>
    public EngineProtocol DetectedProtocol => protocol;

    /// <summary>引擎是否為 Pikafish（大小寫不敏感）。</summary>
    public bool IsPikafish => engineName.Contains("Pikafish", StringComparison.OrdinalIgnoreCase);

    /// <summary>引擎顯示標籤：握手後回傳引擎名稱，握手前回傳「外部引擎」。</summary>
    public string EngineLabel => string.IsNullOrEmpty(engineName) ? "外部引擎" : engineName;

    // ─── 建構子 ───────────────────────────────────────────────────────────

    public ExternalEngineAdapter(string executablePath, EngineProtocol protocol)
    {
        this.executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        this.protocol = protocol;
    }

    // ─── 靜態 Factory：自動偵測協議（UCCI 優先）────────────────────────────

    /// <summary>
    /// 自動偵測協議並連線：先嘗試 UCCI（5 秒超時），失敗則嘗試 UCI。
    /// 成功返回已初始化的 adapter；兩者都失敗則拋出最後一個例外。
    /// UCCI 優先是因為本專案主要支援中國象棋引擎（如 Pikafish），預設使用 UCCI 協議。
    /// </summary>
    public static async Task<ExternalEngineAdapter> DetectAndConnectAsync(
        string executablePath, CancellationToken ct = default)
    {
        // 嘗試 UCCI
        var ucciAdapter = new ExternalEngineAdapter(executablePath, EngineProtocol.Ucci);
        try
        {
            using var ucciCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ucciCts.CancelAfter(TimeSpan.FromSeconds(5));
            await ucciAdapter.InitializeAsync(ucciCts.Token);
            return ucciAdapter;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            ucciAdapter.Dispose();
        }

        // 嘗試 UCI（附加獨立超時，避免非引擎 .exe 導致無限等待）
        var uciAdapter = new ExternalEngineAdapter(executablePath, EngineProtocol.Uci);
        using var uciCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        uciCts.CancelAfter(TimeSpan.FromSeconds(10));
        await uciAdapter.InitializeAsync(uciCts.Token);
        return uciAdapter;
    }

    // ─── 初始化 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 啟動外部引擎 process 並完成握手序列。
    /// 可重複呼叫（已初始化則立刻返回）。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await initLock.WaitAsync(ct);
        try
        {
            if (initialized) return;
            StartProcess();
            await HandshakeAsync(ct);
            initialized = true;
        }
        finally
        {
            initLock.Release();
        }
    }

    private void StartProcess()
    {
        // 驗證路徑：正規化後確認為存在的 .exe 檔案，防止路徑遍歷與非預期程式啟動
        // 此專案為 WPF（Windows-only），.exe 副檔名限制為有意設計
        string fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"引擎執行檔不存在：{fullPath}");
        if (!Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"引擎執行檔必須為 .exe 檔案：{fullPath}");

        var psi = new ProcessStartInfo(fullPath)
        {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            StandardInputEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        process = Process.Start(psi) ?? throw new InvalidOperationException($"無法啟動外部引擎：{fullPath}");
        input = process.StandardInput;
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        if (protocol == EngineProtocol.Ucci)
        {
            // UCCI 握手：ucci → 等待 ucciresp 或 ucciok
            await SendLineAsync("ucci");
            await WaitForHandshakeCompletionAsync(line => line == "ucciresp" || line == "ucciok", ct);
        }
        else
        {
            // UCI 握手：uci → 等待 uciok → 設定象棋變體
            await SendLineAsync("uci");
            await WaitForHandshakeCompletionAsync(line => line == "uciok", ct);
            await SendLineAsync("setoption name UCI_Variant value xiangqi");
        }

        await SendLineAsync("isready");
        await WaitForLineAsync(line => line == "readyok", ct);
    }

    // ─── IAiEngine.SearchAsync ────────────────────────────────────────────

    public async Task<SearchResult> SearchAsync(
        IBoard board,
        SearchSettings settings,
        CancellationToken ct = default,
        IProgress<SearchProgress>? progress = null)
    {
        await InitializeAsync(ct);

        // 送出局面
        await SendLineAsync($"position fen {board.ToFen()}");

        // 送出 go 命令
        int timeLimitMs = settings.EffectiveHardLimitMs > 0 ? settings.EffectiveHardLimitMs : 3000;
        await SendLineAsync($"go movetime {timeLimitMs}");

        // 等待 bestmove，同時解析 info 行更新進度
        string bestMoveStr = "a0a0";
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            bestMoveStr = await WaitForBestMoveAsync(progress, linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 外部取消 → 送 stop
            await SendLineAsync("stop");
            // 仍等一下 bestmove（引擎必須回應 stop 後才送 bestmove）
            try
            {
                using var stopCts = new CancellationTokenSource(2000);
                bestMoveStr = await WaitForBestMoveAsync(null, stopCts.Token);
            }
            catch
            {
                // 若等不到就用空走法
            }
            throw;
        }

        // 解析 bestmove（可能為 "(none)"）
        if (bestMoveStr == "(none)" || string.IsNullOrEmpty(bestMoveStr))
            return new SearchResult { BestMove = Move.Null };

        try
        {
            var bestMove = UcciNotation.UcciToMove(bestMoveStr);
            return new SearchResult { BestMove = bestMove, Depth = settings.Depth };
        }
        catch
        {
            return new SearchResult { BestMove = Move.Null };
        }
    }

    // ─── IAiEngine 評估方法（近似實作：逐一呼叫 SearchAsync） ─────────────

    public async Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesAsync(
        IBoard board,
        IEnumerable<Move> moves,
        int depth,
        CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        await InitializeAsync(ct);
        var results = new List<MoveEvaluation>();
        var settings = new SearchSettings { Depth = depth, TimeLimitMs = 500 };

        foreach (var move in moves)
        {
            ct.ThrowIfCancellationRequested();
            var cloned = board.Clone();
            cloned.MakeMove(move);
            var result = await SearchAsync(cloned, settings, ct);
            results.Add(new MoveEvaluation { Move = move, Score = -result.Score });
        }

        return results;
    }

    // ─── TT 方法（全部 stub，外部引擎自管 TT） ────────────────────────────

    public Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default)
        => Task.CompletedTask;

    public TTStatistics GetTTStatistics() => new TTStatistics();

    public IAiEngine CloneWithCopiedTT() => new ExternalEngineAdapter(executablePath, protocol);

    public IAiEngine CloneWithEmptyTT() => new ExternalEngineAdapter(executablePath, protocol);

    public void MergeTranspositionTableFrom(IAiEngine other) { }

    public IEnumerable<TTEntry> EnumerateTTEntries() => Enumerable.Empty<TTEntry>();

    public TTTreeNode? ExploreTTTree(IBoard board, int maxDepth = 6) => null;

    /// <summary>外部引擎不支援 Multi-PV；退化為單一最佳著法。</summary>
    public async Task<IReadOnlyList<MoveEvaluation>> SearchMultiPvAsync(
        IBoard board, SearchSettings settings, int pvCount,
        CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
    {
        var result = await SearchAsync(board, settings, ct, progress);
        if (result.BestMove.IsNull) return [];
        return
        [
            new MoveEvaluation
            {
                Move = result.BestMove,
                Score = result.Score,
                IsBest = true,
                PvLine = result.PvLine
            }
        ];
    }

    // ─── Dispose ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        try
        {
            if (initialized && input != null)
            {
                input.WriteLine("quit");
                input.Flush();
            }
        }
        catch { /* 忽略退出時的 IO 錯誤 */ }

        // 給引擎最多 2 秒優雅退出，逾時才強制 Kill
        try
        {
            if (process != null && !process.HasExited && !process.WaitForExit(2000))
                process.Kill(entireProcessTree: true);
        }
        catch { }
        try { process?.Dispose(); } catch { }

        input?.Dispose();
        initLock.Dispose();
    }

    // ─── 私有通訊輔助 ─────────────────────────────────────────────────────

    private async Task SendLineAsync(string line)
    {
        if (input == null) throw new InvalidOperationException("引擎尚未啟動");
        await input.WriteLineAsync(line);
        await input.FlushAsync();
    }

    /// <summary>
    /// 讀取握手期間的所有行，直到符合完成條件，
    /// 同時擷取 "id name ..." 行以偵測引擎身份。
    /// </summary>
    private Task WaitForHandshakeCompletionAsync(Func<string, bool> doneCondition, CancellationToken ct)
        => WaitForLineAsync(doneCondition, ct, line =>
        {
            if (line.StartsWith("id name ", StringComparison.Ordinal))
                engineName = line["id name ".Length..].Trim();

            if (line.StartsWith("id author ", StringComparison.Ordinal))
                engineAuthor = line["id author ".Length..].Trim();

            // 解析 UCI_Elo：取 "option name UCI_Elo type spin default <N>" 中的 default 值
            if (line.StartsWith("option name UCI_Elo ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(' ');
                int defaultIdx = Array.IndexOf(parts, "default");
                if (defaultIdx >= 0 && defaultIdx + 1 < parts.Length
                    && int.TryParse(parts[defaultIdx + 1], out int elo))
                    eloRating = elo;
            }
        });

    /// <summary>向引擎發送 setoption name X value Y 命令。</summary>
    public Task SendOptionAsync(string name, string value)
        => SendLineAsync($"setoption name {SanitizeUciToken(name)} value {SanitizeUciToken(value)}");

    /// <summary>向引擎發送無值選項（如 Clear Hash）。</summary>
    public Task SendButtonOptionAsync(string name)
        => SendLineAsync($"setoption name {SanitizeUciToken(name)}");

    /// <summary>移除換行符，防止注入額外 UCI 命令。</summary>
    private static string SanitizeUciToken(string token)
        => token.Replace("\r", "").Replace("\n", "");

    /// <summary>
    /// 持續讀取引擎 stdout，直到找到符合 <paramref name="predicate"/> 的行。
    /// 可選 <paramref name="onEachLine"/> 回呼在判斷前對每行進行副作用處理（如擷取引擎名稱）。
    /// </summary>
    private async Task WaitForLineAsync(Func<string, bool> predicate, CancellationToken ct, Action<string>? onEachLine = null)
    {
        if (process == null) throw new InvalidOperationException("引擎尚未啟動");

        while (!ct.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line == null) throw new EndOfStreamException("引擎 process 已結束（stdout 已關閉）");
            line = line.Trim();
            onEachLine?.Invoke(line);
            if (predicate(line)) return;
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>持續讀取 stdout，直到收到 "bestmove" 行；回傳走法字串部分。</summary>
    private async Task<string> WaitForBestMoveAsync(IProgress<SearchProgress>? progress, CancellationToken ct)
    {
        if (process == null) throw new InvalidOperationException("引擎尚未啟動");

        while (!ct.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line == null) throw new EndOfStreamException("引擎 process 已結束");

            line = line.Trim();

            if (line.StartsWith("info "))
            {
                // 解析 info 行並更新進度
                TryReportProgress(line, progress);
                continue;
            }

            if (line.StartsWith("bestmove "))
            {
                // bestmove <move> [ponder <move>]
                var tokens = line.Split(' ');
                return tokens.Length >= 2 ? tokens[1] : "(none)";
            }
        }

        ct.ThrowIfCancellationRequested();
        return "(none)";
    }

    /// <summary>從 "info depth N score cp S nodes N nps N" 解析並回報進度。</summary>
    private static void TryReportProgress(string infoLine, IProgress<SearchProgress>? progress)
    {
        if (progress == null) return;

        var tokens = infoLine.Split(' ');
        var p = new SearchProgress();

        for (int i = 1; i < tokens.Length - 1; i++)
        {
            switch (tokens[i])
            {
                case "depth" when int.TryParse(tokens[i + 1], out int d):
                    p.CurrentDepth = d; break;
                case "score" when i + 2 < tokens.Length && tokens[i + 1] == "cp" && int.TryParse(tokens[i + 2], out int sc):
                    p.Score = sc; break;
                case "nodes" when long.TryParse(tokens[i + 1], out long n):
                    p.Nodes = n; break;
                case "nps" when long.TryParse(tokens[i + 1], out long nps):
                    p.NodesPerSecond = nps; break;
                case "time" when long.TryParse(tokens[i + 1], out long t):
                    p.ElapsedMs = t; break;
            }
        }

        progress.Report(p);
    }
}
