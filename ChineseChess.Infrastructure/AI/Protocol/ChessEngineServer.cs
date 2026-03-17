using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Infrastructure.AI.Protocol;

/// <summary>
/// 雙協議（UCI / UCCI）象棋引擎 TCP 伺服器。
///
/// 每個連入的客戶端在獨立 Task 中處理。
/// 第一行命令用於偵測協議（uci → UCI 模式；ucci → UCCI 模式）。
/// 搜尋由注入的 <see cref="IAiEngine"/> 執行。
/// </summary>
public sealed class ChessEngineServer : IChessEngineServer
{
    private readonly IAiEngine engine;
    private TcpListener? listener;
    private CancellationTokenSource? serverCts;
    private Task? acceptTask;

    // 活躍連線集合（連線 Task → true）
    private readonly ConcurrentDictionary<Task, byte> activeTasks = new();

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }

    public event Action<string>? StatusChanged;

    // ─── 建構子 ───────────────────────────────────────────────────────────

    public ChessEngineServer(IAiEngine engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    // ─── 啟動 / 停止 ──────────────────────────────────────────────────────

    public Task StartAsync(int port, CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("伺服器已在運行中");

        Port = port;
        serverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        IsRunning = true;

        StatusChanged?.Invoke($"伺服器已啟動，監聽 127.0.0.1:{port}");

        acceptTask = AcceptLoopAsync(serverCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        IsRunning = false;
        serverCts?.Cancel();

        try { listener?.Stop(); } catch { }

        if (acceptTask != null)
        {
            try { await acceptTask; } catch { }
        }

        // 等待所有連線 Task 結束（最多 2 秒）
        using var timeout = new CancellationTokenSource(2000);
        foreach (var task in activeTasks.Keys)
        {
            try { await task.WaitAsync(timeout.Token); } catch { }
        }

        activeTasks.Clear();
        StatusChanged?.Invoke("伺服器已停止");
    }

    // ─── 接受連線迴圈 ─────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcpClient = await listener!.AcceptTcpClientAsync(ct);
                StatusChanged?.Invoke($"新連線：{tcpClient.Client.RemoteEndPoint}");

                var connectionTask = HandleConnectionAsync(tcpClient, ct);
                activeTasks.TryAdd(connectionTask, 0);

                // 連線結束後自動移除
                _ = connectionTask.ContinueWith(t => activeTasks.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (!IsRunning) { /* 正常關閉 */ }
    }

    // ─── 單一連線處理 ─────────────────────────────────────────────────────

    private async Task HandleConnectionAsync(TcpClient tcpClient, CancellationToken serverToken)
    {
        using var client = tcpClient;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);

        try
        {
            // 偵測協議（讀第一行）
            var firstLine = await reader.ReadLineAsync(connCts.Token);
            if (firstLine == null) return;

            firstLine = firstLine.Trim();
            bool isUcci = firstLine == "ucci";

            if (isUcci)
            {
                // UCCI 握手
                await writer.WriteLineAsync("id name InternalEngine");
                await writer.WriteLineAsync("id author ChineseChess");
                await writer.WriteLineAsync("ucciresp");
            }
            else if (firstLine == "uci")
            {
                // UCI 握手
                await writer.WriteLineAsync("id name InternalEngine");
                await writer.WriteLineAsync("id author ChineseChess");
                await writer.WriteLineAsync("option name UCI_Variant type combo default xiangqi var xiangqi");
                await writer.WriteLineAsync("uciok");
            }
            else
            {
                // 不認識的握手命令
                return;
            }

            // 命令處理迴圈
            IBoard? currentBoard = null;
            CancellationTokenSource? searchCts = null;

            while (!connCts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(connCts.Token);
                if (line == null) break;

                line = line.Trim();
                var args = ChessProtocolParser.Parse(line);

                switch (args.Command)
                {
                    case ChessCommand.IsReady:
                        await writer.WriteLineAsync("readyok");
                        break;

                    case ChessCommand.Position:
                        currentBoard = ParsePosition(args);
                        break;

                    case ChessCommand.Go:
                        if (currentBoard != null)
                        {
                            searchCts?.Cancel();
                            searchCts = CancellationTokenSource.CreateLinkedTokenSource(connCts.Token);
                            _ = RunSearchAsync(currentBoard.Clone(), args, writer, searchCts.Token);
                        }
                        break;

                    case ChessCommand.Stop:
                        searchCts?.Cancel();
                        break;

                    case ChessCommand.Quit:
                        goto endConnection;

                    case ChessCommand.Uci when !isUcci:
                    case ChessCommand.Ucci when isUcci:
                        // 重複握手（部分引擎管理軟體可能發送）
                        await writer.WriteLineAsync(isUcci ? "ucciresp" : "uciok");
                        break;
                }
            }

            endConnection:
            searchCts?.Cancel();
            StatusChanged?.Invoke($"連線斷開：{client.Client.RemoteEndPoint}");
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"連線錯誤：{ex.Message}");
        }
    }

    // ─── 搜尋並回傳 bestmove ──────────────────────────────────────────────

    private async Task RunSearchAsync(IBoard board, ChessCommandArgs args, StreamWriter writer, CancellationToken ct)
    {
        var settings = new SearchSettings
        {
            Depth        = args.Depth ?? 20,
            TimeLimitMs  = args.MoveTime ?? (args.IsInfinite ? int.MaxValue / 2 : 3000),
            ThreadCount  = Environment.ProcessorCount
        };

        try
        {
            var result = await engine.SearchAsync(board, settings, ct);
            string moveStr = result.BestMove.IsNull ? "(none)" : UcciNotation.MoveToUcci(result.BestMove);
            await writer.WriteLineAsync($"bestmove {moveStr}");
        }
        catch (OperationCanceledException)
        {
            // stop 後仍需送出 bestmove（送出目前最佳走法或 none）
            try { await writer.WriteLineAsync("bestmove (none)"); } catch { }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"搜尋錯誤：{ex.Message}");
            try { await writer.WriteLineAsync("bestmove (none)"); } catch { }
        }
    }

    // ─── FEN 解析輔助 ─────────────────────────────────────────────────────

    private static IBoard? ParsePosition(ChessCommandArgs args)
    {
        if (string.IsNullOrEmpty(args.Fen)) return null;

        var board = new Board();
        try
        {
            board.ParseFen(args.Fen);
        }
        catch
        {
            return null;
        }

        // 應用走法序列
        foreach (var moveStr in args.Moves)
        {
            try
            {
                var move = UcciNotation.UcciToMove(moveStr);
                board.MakeMove(move);
            }
            catch
            {
                break; // 無效走法，停止應用
            }
        }

        return board;
    }

    // ─── IAsyncDisposable ─────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        serverCts?.Dispose();
    }
}
