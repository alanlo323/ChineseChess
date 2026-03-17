using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// ChessEngineServer 整合測試。
/// 測試使用 TcpClient 直接連線到本機伺服器，驗證協議握手和命令處理。
/// 使用隨機埠號避免衝突。
/// </summary>
public class ChessEngineServerTests : IAsyncLifetime
{
    private readonly MockAiEngine mockEngine = new();
    private ChessEngineServer? server;
    private int port;

    // ─── 測試替身 ─────────────────────────────────────────────────────────

    private sealed class MockAiEngine : IAiEngine
    {
        public Move ReturnMove { get; set; } = new Move(70, 67); // h2e2

        public Task<SearchResult> SearchAsync(IBoard board, SearchSettings settings, CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
            => Task.FromResult(new SearchResult { BestMove = ReturnMove, Score = 100, Depth = 5 });

        public Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesAsync(IBoard board, IEnumerable<Move> moves, int depth, CancellationToken ct = default, IProgress<string>? progress = null)
            => Task.FromResult<IReadOnlyList<MoveEvaluation>>([]);
        public Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public TTStatistics GetTTStatistics() => new TTStatistics();
        public IAiEngine CloneWithCopiedTT() => this;
        public IAiEngine CloneWithEmptyTT()  => this;
        public void MergeTranspositionTableFrom(IAiEngine other) { }
        public IEnumerable<TTEntry> EnumerateTTEntries() => [];
        public TTTreeNode? ExploreTTTree(IBoard board, int maxDepth = 6) => null;
    }

    /// <summary>包裝 TcpClient 連線的可 Dispose 輔助型別。</summary>
    private sealed class TestConnection : IDisposable
    {
        private readonly TcpClient client;
        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }

        public TestConnection(TcpClient client)
        {
            this.client = client;
            var stream = client.GetStream();
            Reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            Writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        }

        public void Dispose()
        {
            Reader.Dispose();
            Writer.Dispose();
            client.Dispose();
        }
    }

    // ─── IAsyncLifetime ───────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        port   = GetFreeTcpPort();
        server = new ChessEngineServer(mockEngine);
        await server.StartAsync(port);
    }

    public async Task DisposeAsync()
    {
        if (server != null)
            await server.DisposeAsync();
    }

    // ─── 基本狀態測試 ─────────────────────────────────────────────────────

    [Fact]
    public void Server_AfterStart_IsRunning()
    {
        Assert.True(server!.IsRunning);
        Assert.Equal(port, server.Port);
    }

    [Fact]
    public async Task Server_AfterStop_IsNotRunning()
    {
        await server!.StopAsync();
        Assert.False(server.IsRunning);
    }

    // ─── UCCI 協議握手 ────────────────────────────────────────────────────

    [Fact]
    public async Task Ucci_Handshake_ShouldReceiveUcciresp()
    {
        using var conn = await ConnectAsync();

        await conn.Writer.WriteLineAsync("ucci");
        string response = await ReadUntilLineAsync(conn.Reader, "ucciresp");
        Assert.Equal("ucciresp", response);
    }

    [Fact]
    public async Task Ucci_IsReady_ShouldReceiveReadyok()
    {
        using var conn = await ConnectAsync();

        await conn.Writer.WriteLineAsync("ucci");
        await ReadUntilLineAsync(conn.Reader, "ucciresp");

        await conn.Writer.WriteLineAsync("isready");
        string response = await ReadLineWithTimeoutAsync(conn.Reader);
        Assert.Equal("readyok", response);
    }

    // ─── UCI 協議握手 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Uci_Handshake_ShouldReceiveUciok()
    {
        using var conn = await ConnectAsync();

        await conn.Writer.WriteLineAsync("uci");
        string response = await ReadUntilLineAsync(conn.Reader, "uciok");
        Assert.Equal("uciok", response);
    }

    [Fact]
    public async Task Uci_HandshakeContainsOptionLine()
    {
        using var conn = await ConnectAsync();

        await conn.Writer.WriteLineAsync("uci");

        var lines = new List<string>();
        string? line;
        using var cts = new CancellationTokenSource(3000);
        while ((line = await conn.Reader.ReadLineAsync(cts.Token)) != null)
        {
            lines.Add(line);
            if (line == "uciok") break;
        }

        Assert.Contains(lines, l => l.Contains("UCI_Variant"));
    }

    [Fact]
    public async Task Uci_IsReady_ShouldReceiveReadyok()
    {
        using var conn = await ConnectAsync();

        await conn.Writer.WriteLineAsync("uci");
        await ReadUntilLineAsync(conn.Reader, "uciok");

        await conn.Writer.WriteLineAsync("isready");
        string response = await ReadLineWithTimeoutAsync(conn.Reader);
        Assert.Equal("readyok", response);
    }

    // ─── position + go → bestmove ────────────────────────────────────────

    [Fact]
    public async Task Go_ShouldReceiveBestmove()
    {
        const string fen = "rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1";
        using var conn = await ConnectAsync();

        await conn.Writer.WriteLineAsync("ucci");
        await ReadUntilLineAsync(conn.Reader, "ucciresp");
        await conn.Writer.WriteLineAsync("isready");
        await ReadLineWithTimeoutAsync(conn.Reader); // readyok

        await conn.Writer.WriteLineAsync($"position fen {fen}");
        await conn.Writer.WriteLineAsync("go movetime 100");

        string bestmoveLine = await ReadUntilPrefixAsync(conn.Reader, "bestmove");
        Assert.StartsWith("bestmove ", bestmoveLine);
    }

    // ─── quit → 連線關閉 ──────────────────────────────────────────────────

    [Fact]
    public async Task Quit_ShouldCloseConnectionButServerKeepsRunning()
    {
        using var conn = await ConnectAsync();

        await conn.Writer.WriteLineAsync("ucci");
        await ReadUntilLineAsync(conn.Reader, "ucciresp");
        await conn.Writer.WriteLineAsync("quit");

        await Task.Delay(200);
        Assert.True(server!.IsRunning);
    }

    // ─── 多連線（各自獨立協議偵測） ──────────────────────────────────────

    [Fact]
    public async Task MultipleConnections_EachDetectsProtocolIndependently()
    {
        using var conn1 = await ConnectAsync();
        using var conn2 = await ConnectAsync();

        await conn1.Writer.WriteLineAsync("ucci");
        await conn2.Writer.WriteLineAsync("uci");

        string resp1 = await ReadUntilLineAsync(conn1.Reader, "ucciresp");
        string resp2 = await ReadUntilLineAsync(conn2.Reader, "uciok");

        Assert.Equal("ucciresp", resp1);
        Assert.Equal("uciok", resp2);
    }

    // ─── 私有輔助 ─────────────────────────────────────────────────────────

    private async Task<TestConnection> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        return new TestConnection(client);
    }

    private static async Task<string> ReadLineWithTimeoutAsync(StreamReader reader, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var line = await reader.ReadLineAsync(cts.Token);
        return line?.Trim() ?? string.Empty;
    }

    private static async Task<string> ReadUntilLineAsync(StreamReader reader, string target, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            line = line.Trim();
            if (line == target) return line;
        }
        return string.Empty;
    }

    private static async Task<string> ReadUntilPrefixAsync(StreamReader reader, string prefix, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            line = line.Trim();
            if (line.StartsWith(prefix)) return line;
        }
        return string.Empty;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int p = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return p;
    }
}
