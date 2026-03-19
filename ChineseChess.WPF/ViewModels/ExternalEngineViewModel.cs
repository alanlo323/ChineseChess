using ChineseChess.Application.Configuration;
using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Infrastructure.AI.Protocol;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// 外部引擎 / 雙協議伺服器設定的 ViewModel。
/// 對應 ExternalEngineView.xaml 的功能：
///   - 紅方 / 黑方可各自選擇 UCI 或 UCCI 外部引擎（.exe）
///   - 伺服器啟動 / 停止（同時接受 UCI 和 UCCI 客戶端）
/// </summary>
public class ExternalEngineViewModel : ObservableObject, IDisposable
{
    private readonly IEngineProvider engineProvider;
    private readonly IChessEngineServer engineServer;
    private readonly IUserSettingsService userSettingsService;

    // ─── 紅方外部引擎欄位 ─────────────────────────────────────────────────
    private bool useRedExternalEngine;
    private string redEnginePath = string.Empty;
    private EngineProtocol redProtocol = EngineProtocol.Ucci;
    private string redEngineStatus = "未啟用";

    // ─── 黑方外部引擎欄位 ─────────────────────────────────────────────────
    private bool useBlackExternalEngine;
    private string blackEnginePath = string.Empty;
    private EngineProtocol blackProtocol = EngineProtocol.Ucci;
    private string blackEngineStatus = "未啟用";

    // ─── 伺服器欄位 ───────────────────────────────────────────────────────
    private bool isServerRunning;
    private int serverPort = 23333;
    private string serverStatus = "伺服器已停止";

    // ─── 建構子 ───────────────────────────────────────────────────────────

    public ExternalEngineViewModel(IEngineProvider engineProvider, IChessEngineServer engineServer, IUserSettingsService userSettingsService)
    {
        this.engineProvider     = engineProvider     ?? throw new ArgumentNullException(nameof(engineProvider));
        this.engineServer       = engineServer       ?? throw new ArgumentNullException(nameof(engineServer));
        this.userSettingsService = userSettingsService ?? throw new ArgumentNullException(nameof(userSettingsService));

        this.engineServer.StatusChanged += OnServerStatusChanged;

        BrowseRedEngineCommand   = new RelayCommand(_ => BrowseEngine(isRed: true));
        BrowseBlackEngineCommand = new RelayCommand(_ => BrowseEngine(isRed: false));
        ApplyRedEngineCommand    = new RelayCommand(async _ => await ApplyEngineAsync(isRed: true));
        ApplyBlackEngineCommand  = new RelayCommand(async _ => await ApplyEngineAsync(isRed: false));
        StartServerCommand       = new RelayCommand(async _ => await StartServerAsync(), _ => !IsServerRunning);
        StopServerCommand        = new RelayCommand(async _ => await StopServerAsync(),  _ => IsServerRunning);

        _ = LoadAndAutoConnectAsync().ContinueWith(
            t => System.Diagnostics.Trace.TraceWarning($"自動連接引擎失敗：{t.Exception?.InnerException?.Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // ─── 紅方屬性 ─────────────────────────────────────────────────────────

    public bool UseRedExternalEngine
    {
        get => useRedExternalEngine;
        set => SetProperty(ref useRedExternalEngine, value);
    }

    public string RedEnginePath
    {
        get => redEnginePath;
        set
        {
            if (SetProperty(ref redEnginePath, value))
                OnPropertyChanged(nameof(HasRedEngineConfig));
        }
    }

    public EngineProtocol RedProtocol
    {
        get => redProtocol;
        set => SetProperty(ref redProtocol, value);
    }

    public string RedEngineStatus
    {
        get => redEngineStatus;
        set => SetProperty(ref redEngineStatus, value);
    }

    // ─── 黑方屬性 ─────────────────────────────────────────────────────────

    public bool UseBlackExternalEngine
    {
        get => useBlackExternalEngine;
        set => SetProperty(ref useBlackExternalEngine, value);
    }

    public string BlackEnginePath
    {
        get => blackEnginePath;
        set
        {
            if (SetProperty(ref blackEnginePath, value))
                OnPropertyChanged(nameof(HasBlackEngineConfig));
        }
    }

    public EngineProtocol BlackProtocol
    {
        get => blackProtocol;
        set => SetProperty(ref blackProtocol, value);
    }

    public string BlackEngineStatus
    {
        get => blackEngineStatus;
        set => SetProperty(ref blackEngineStatus, value);
    }

    // ─── 伺服器屬性 ───────────────────────────────────────────────────────

    public bool IsServerRunning
    {
        get => isServerRunning;
        set
        {
            if (SetProperty(ref isServerRunning, value))
            {
                OnPropertyChanged(nameof(ServerButtonLabel));
                OnPropertyChanged(nameof(IsServerStopped));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int ServerPort
    {
        get => serverPort;
        set => SetProperty(ref serverPort, value);
    }

    public string ServerStatus
    {
        get => serverStatus;
        set => SetProperty(ref serverStatus, value);
    }

    public string ServerButtonLabel => IsServerRunning ? "停止伺服器" : "啟動伺服器";

    /// <summary>伺服器停止時為 true（供 IsEnabled 綁定使用）。</summary>
    public bool IsServerStopped => !IsServerRunning;

    // ─── 協議選項 ─────────────────────────────────────────────────────────

    public IEnumerable<EngineProtocol> EngineProtocols => Enum.GetValues<EngineProtocol>();

    // ─── 供設定頁快速開關使用的 API ──────────────────────────────────────

    /// <summary>紅方已設定引擎路徑（供設定頁 CheckBox IsEnabled 綁定）。</summary>
    public bool HasRedEngineConfig => !string.IsNullOrWhiteSpace(RedEnginePath);

    /// <summary>黑方已設定引擎路徑（供設定頁 CheckBox IsEnabled 綁定）。</summary>
    public bool HasBlackEngineConfig => !string.IsNullOrWhiteSpace(BlackEnginePath);

    // ─── 命令 ─────────────────────────────────────────────────────────────

    public ICommand BrowseRedEngineCommand   { get; }
    public ICommand BrowseBlackEngineCommand { get; }
    public ICommand ApplyRedEngineCommand    { get; }
    public ICommand ApplyBlackEngineCommand  { get; }
    public ICommand StartServerCommand       { get; }
    public ICommand StopServerCommand        { get; }

    // ─── 私有命令實作 ─────────────────────────────────────────────────────

    private void BrowseEngine(bool isRed)
    {
        var dialog = new OpenFileDialog
        {
            Title  = isRed ? "選擇紅方引擎" : "選擇黑方引擎",
            Filter = "可執行檔|*.exe|所有檔案|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        if (isRed)
            RedEnginePath = dialog.FileName;
        else
            BlackEnginePath = dialog.FileName;
    }

    private async Task ApplyEngineAsync(bool isRed)
    {
        var path     = isRed ? RedEnginePath     : BlackEnginePath;
        var protocol = isRed ? RedProtocol       : BlackProtocol;
        var useExt   = isRed ? UseRedExternalEngine : UseBlackExternalEngine;

        if (!useExt)
        {
            // 停用外部引擎，恢復內建
            if (isRed)
            {
                engineProvider.SetRedExternalEngine(null);
                RedEngineStatus = "已恢復內建引擎";
            }
            else
            {
                engineProvider.SetBlackExternalEngine(null);
                BlackEngineStatus = "已恢復內建引擎";
            }
            PersistCurrentSettings();
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            if (isRed) RedEngineStatus   = "請先選擇引擎執行檔";
            else       BlackEngineStatus = "請先選擇引擎執行檔";
            return;
        }

        if (isRed) RedEngineStatus   = "初始化中...";
        else       BlackEngineStatus = "初始化中...";

        var adapter = new ExternalEngineAdapter(path, protocol);
        try
        {
            using var cts = new CancellationTokenSource(10_000); // 10 秒超時
            await adapter.InitializeAsync(cts.Token);

            if (isRed)
            {
                engineProvider.SetRedExternalEngine(adapter);
                RedEngineStatus = $"已載入（{protocol}）";
            }
            else
            {
                engineProvider.SetBlackExternalEngine(adapter);
                BlackEngineStatus = $"已載入（{protocol}）";
            }

            PersistCurrentSettings();
        }
        catch (Exception ex)
        {
            adapter.Dispose();
            if (isRed) RedEngineStatus   = $"初始化失敗：{ex.Message}";
            else       BlackEngineStatus = $"初始化失敗：{ex.Message}";
        }
    }

    /// <summary>
    /// 將目前引擎設定快照寫入持久化儲存。
    /// </summary>
    private void PersistCurrentSettings()
    {
        var snapshot = new ExternalEngineSettings
        {
            UseRedExternalEngine  = UseRedExternalEngine,
            RedEnginePath         = RedEnginePath,
            RedProtocol           = RedProtocol,
            UseBlackExternalEngine = UseBlackExternalEngine,
            BlackEnginePath       = BlackEnginePath,
            BlackProtocol         = BlackProtocol,
            ServerPort            = ServerPort
        };
        userSettingsService.SaveEngineSettings(snapshot);
    }

    /// <summary>
    /// 從設定頁快速切換外部引擎（回傳 false 代表尚未設定路徑）。
    /// </summary>
    public async Task<bool> ToggleEngineAsync(bool isRed, bool enable)
    {
        var path = isRed ? RedEnginePath : BlackEnginePath;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (isRed) UseRedExternalEngine   = enable;
        else       UseBlackExternalEngine = enable;

        await ApplyEngineAsync(isRed);
        return true;
    }

    /// <summary>
    /// 啟動時載入持久化設定，並自動連接已設定的外部引擎。
    /// </summary>
    private async Task LoadAndAutoConnectAsync()
    {
        try
        {
            var saved = userSettingsService.LoadEngineSettings();

            // 在 UI 執行緒還原欄位，ConfigureAwait(false) 確保後續引擎初始化不佔用 UI 執行緒
            var app = System.Windows.Application.Current;
            if (app != null)
                await app.Dispatcher.InvokeAsync(() => RestoreSettings(saved)).Task.ConfigureAwait(false);
            else
                RestoreSettings(saved);

            // 自動連接（在背景執行緒執行，不阻塞 UI）
            if (saved.UseRedExternalEngine && !string.IsNullOrWhiteSpace(saved.RedEnginePath))
                await ApplyEngineAsync(isRed: true);

            if (saved.UseBlackExternalEngine && !string.IsNullOrWhiteSpace(saved.BlackEnginePath))
                await ApplyEngineAsync(isRed: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"自動載入引擎設定失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// 將持久化設定還原到 backing fields，並通知 UI 屬性變更（必須在 UI 執行緒呼叫）。
    /// </summary>
    private void RestoreSettings(ExternalEngineSettings saved)
    {
        redEnginePath          = saved.RedEnginePath;
        redProtocol            = saved.RedProtocol;
        useRedExternalEngine   = saved.UseRedExternalEngine;
        blackEnginePath        = saved.BlackEnginePath;
        blackProtocol          = saved.BlackProtocol;
        useBlackExternalEngine = saved.UseBlackExternalEngine;
        serverPort             = saved.ServerPort;

        OnPropertyChanged(nameof(RedEnginePath));
        OnPropertyChanged(nameof(RedProtocol));
        OnPropertyChanged(nameof(UseRedExternalEngine));
        OnPropertyChanged(nameof(BlackEnginePath));
        OnPropertyChanged(nameof(BlackProtocol));
        OnPropertyChanged(nameof(UseBlackExternalEngine));
        OnPropertyChanged(nameof(ServerPort));
        OnPropertyChanged(nameof(HasRedEngineConfig));
        OnPropertyChanged(nameof(HasBlackEngineConfig));
    }

    private async Task StartServerAsync()
    {
        try
        {
            await engineServer.StartAsync(ServerPort);
            IsServerRunning = true;
        }
        catch (Exception ex)
        {
            ServerStatus = $"啟動失敗：{ex.Message}";
        }
    }

    private async Task StopServerAsync()
    {
        try
        {
            await engineServer.StopAsync();
            IsServerRunning = false;
        }
        catch (Exception ex)
        {
            ServerStatus = $"停止失敗：{ex.Message}";
        }
    }

    private void OnServerStatusChanged(string message)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ServerStatus    = message;
            IsServerRunning = engineServer.IsRunning;
        });
    }

    // ─── Dispose ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        engineServer.StatusChanged -= OnServerStatusChanged;
    }
}
