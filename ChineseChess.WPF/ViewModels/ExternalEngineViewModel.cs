using ChineseChess.Application.Interfaces;
using ChineseChess.WPF.Core;
using System;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// 引擎伺服器 ViewModel（僅保留 UCI/UCCI 雙協議伺服器功能）。
/// 外部引擎的 per-player 設定已遷移至 AiPlayerSettingsViewModel。
/// </summary>
public class ExternalEngineViewModel : ObservableObject, IDisposable
{
    private readonly IChessEngineServer engineServer;

    private bool isServerRunning;
    private int serverPort = 23333;
    private string serverStatus = "伺服器已停止";

    public ExternalEngineViewModel(IChessEngineServer engineServer, IUserSettingsService userSettingsService)
    {
        this.engineServer = engineServer ?? throw new ArgumentNullException(nameof(engineServer));
        this.engineServer.StatusChanged += OnServerStatusChanged;

        StartServerCommand = new RelayCommand(async _ => await StartServerAsync(), _ => !IsServerRunning);
        StopServerCommand  = new RelayCommand(async _ => await StopServerAsync(),  _ => IsServerRunning);

        // 還原伺服器埠號
        try
        {
            var saved = userSettingsService.LoadEngineSettings();
            serverPort = saved.ServerPort;
        }
        catch { /* 載入失敗使用預設值 */ }
    }

    public bool IsServerRunning
    {
        get => isServerRunning;
        set
        {
            if (SetProperty(ref isServerRunning, value))
            {
                OnPropertyChanged(nameof(ServerButtonLabel));
                OnPropertyChanged(nameof(IsServerStopped));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int ServerPort
    {
        get => serverPort;
        set => SetProperty(ref serverPort, Math.Clamp(value, 1024, 65535));
    }

    public string ServerStatus
    {
        get => serverStatus;
        set => SetProperty(ref serverStatus, value);
    }

    public string ServerButtonLabel => IsServerRunning ? "停止伺服器" : "啟動伺服器";
    public bool IsServerStopped => !IsServerRunning;

    public ICommand StartServerCommand { get; }
    public ICommand StopServerCommand { get; }

    private async System.Threading.Tasks.Task StartServerAsync()
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

    private async System.Threading.Tasks.Task StopServerAsync()
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
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            ServerStatus    = message;
            IsServerRunning = engineServer.IsRunning;
        });
    }

    public void Dispose()
    {
        engineServer.StatusChanged -= OnServerStatusChanged;
    }
}
