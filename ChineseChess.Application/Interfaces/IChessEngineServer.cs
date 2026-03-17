using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Interfaces;

/// <summary>
/// 雙協議（UCI / UCCI）棋類引擎 TCP 伺服器抽象介面。
/// 伺服器監聽指定埠號，自動偵測連入客戶端使用的協議（讀取第一行命令判斷）。
/// </summary>
public interface IChessEngineServer : IAsyncDisposable
{
    /// <summary>伺服器目前是否正在運行中。</summary>
    bool IsRunning { get; }

    /// <summary>伺服器監聽的 TCP 埠號（僅 <see cref="IsRunning"/> 為 true 時有意義）。</summary>
    int Port { get; }

    /// <summary>伺服器狀態變更事件（含啟動、停止、連線/斷線訊息）。</summary>
    event Action<string>? StatusChanged;

    /// <summary>啟動伺服器，開始監聽 <paramref name="port"/>。</summary>
    Task StartAsync(int port, CancellationToken ct = default);

    /// <summary>停止伺服器，關閉所有連線。</summary>
    Task StopAsync();
}
