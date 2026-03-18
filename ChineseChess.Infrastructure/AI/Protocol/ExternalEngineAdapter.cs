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
public sealed class ExternalEngineAdapter : IAiEngine, IDisposable
{
    private readonly string executablePath;
    private readonly EngineProtocol protocol;

    private Process? process;
    private StreamWriter? input;
    private readonly SemaphoreSlim initLock = new SemaphoreSlim(1, 1);
    private bool initialized;
    private bool disposed;

    // ─── 建構子 ───────────────────────────────────────────────────────────

    public ExternalEngineAdapter(string executablePath, EngineProtocol protocol)
    {
        this.executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        this.protocol = protocol;
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
        var psi = new ProcessStartInfo(executablePath)
        {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            StandardInputEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        process = Process.Start(psi) ?? throw new InvalidOperationException($"無法啟動外部引擎：{executablePath}");
        input = process.StandardInput;
        input.AutoFlush = true;
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        if (protocol == EngineProtocol.Ucci)
        {
            // UCCI 握手：ucci → 等待 ucciresp 或 ucciok
            await SendLineAsync("ucci");
            await WaitForLineAsync(line => line == "ucciresp" || line == "ucciok", ct);
        }
        else
        {
            // UCI 握手：uci → 等待 uciok → 設定象棋變體
            await SendLineAsync("uci");
            await WaitForLineAsync(line => line == "uciok", ct);
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

        try { process?.Kill(entireProcessTree: true); } catch { }
        try { process?.Dispose(); } catch { }

        input?.Dispose();
        initLock.Dispose();
    }

    // ─── 私有通訊輔助 ─────────────────────────────────────────────────────

    private Task SendLineAsync(string line)
    {
        if (input == null) throw new InvalidOperationException("引擎尚未啟動");
        input.WriteLine(line);
        return Task.CompletedTask;
    }

    /// <summary>持續讀取引擎 stdout，直到找到符合 <paramref name="predicate"/> 的行。</summary>
    private async Task WaitForLineAsync(Func<string, bool> predicate, CancellationToken ct)
    {
        if (process == null) throw new InvalidOperationException("引擎尚未啟動");

        while (!ct.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line == null) throw new EndOfStreamException("引擎 process 已結束（stdout 已關閉）");
            if (predicate(line.Trim())) return;
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
