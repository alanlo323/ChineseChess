using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Infrastructure.AI.Nnue.Network;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// NNUE 設定頁 ViewModel：
///   - 全域模型載入 / 卸載（PlayerVsAI 及 AIvsAI 共用模式）
///   - 顯示模型元數據（路徑、大小、描述）
///   - 評估模式切換
///   - AIvsAI 每方獨立 NNUE 設定（UsePerPlayerNnue）
///   - 本機訓練面板（Training）
///   - 設定持久化（nnue-user-settings.json）
/// </summary>
public sealed class NnueViewModel : ObservableObject
{
    private readonly INnueNetwork network;
    private readonly INnueSettingsService settingsService;
    private readonly IEngineProvider engineProvider;
    private readonly Lazy<NnueTrainingViewModel> lazyTraining;

    private string modelPath     = string.Empty;
    private string statusMessage = "尚未載入模型";
    private bool isLoading;
    private NnueEvaluationMode evaluationMode = NnueEvaluationMode.Composite;
    private bool usePerPlayerNnue;
    private string perPlayerStatusMessage = string.Empty;
    private bool isApplyingPerPlayer;

    public NnueViewModel(
        INnueNetwork network,
        INnueSettingsService settingsService,
        IEngineProvider engineProvider,
        Lazy<NnueTrainingViewModel> lazyTraining,
        LoadedEngineListViewModel loadedEngineList)
    {
        this.network         = network;
        this.settingsService = settingsService;
        this.engineProvider  = engineProvider;
        this.lazyTraining    = lazyTraining;
        LoadedEngineList     = loadedEngineList ?? throw new ArgumentNullException(nameof(loadedEngineList));

        RedPlayer   = new NnuePlayerViewModel();
        BlackPlayer = new NnuePlayerViewModel();

        // 載入已儲存設定
        var saved = settingsService.LoadNnueSettings();
        modelPath      = saved.ModelFilePath;
        evaluationMode = saved.EvaluationMode;
        usePerPlayerNnue = saved.UsePerPlayerNnue;
        RedPlayer.LoadFrom(saved.RedPlayerSettings);
        BlackPlayer.LoadFrom(saved.BlackPlayerSettings);

        BrowseModelCommand             = new RelayCommand(_ => BrowseModel());
        LoadModelCommand               = new RelayCommand(_ => _ = LoadModelAsync(), _ => !isLoading && File.Exists(ModelPath));
        UnloadModelCommand             = new RelayCommand(_ => UnloadModel(), _ => network.IsLoaded);
        ApplyPerPlayerSettingsCommand  = new RelayCommand(_ => _ = ApplyPerPlayerSettingsAsync(), _ => !isApplyingPerPlayer);

        // 若上次已設定模型路徑且模式非停用，啟動時自動嘗試載入全域模型
        if (!string.IsNullOrEmpty(modelPath)
            && File.Exists(modelPath)
            && evaluationMode != NnueEvaluationMode.Disabled)
        {
            _ = TryAutoLoadModelAsync();
        }

        // 若上次已啟用每方獨立設定，啟動時自動套用
        if (usePerPlayerNnue)
            _ = AutoApplyPerPlayerAsync();
    }

    // ── 全域 NNUE 屬性 ───────────────────────────────────────────────

    public string ModelPath
    {
        get => modelPath;
        set
        {
            if (SetProperty(ref modelPath, value))
            {
                OnPropertyChanged(nameof(CanLoad));
                SaveSettings();
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsLoaded       => network.IsLoaded;
    public bool CanLoad        => !isLoading && File.Exists(ModelPath);

    public NnueModelInfo? ModelInfo => network.ModelInfo;

    public string ModelDescription  => network.ModelInfo?.Description ?? "—";
    public string ModelFileSizeText => network.ModelInfo?.FormattedFileSize ?? "—";
    public string ModelLoadedAt     => network.ModelInfo?.LoadedAt.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    public NnueEvaluationMode EvaluationMode
    {
        get => evaluationMode;
        set
        {
            if (SetProperty(ref evaluationMode, value))
                SaveSettings();
        }
    }

    public IEnumerable<NnueEvaluationMode> EvaluationModes =>
        Enum.GetValues<NnueEvaluationMode>();

    // ── 每方獨立 NNUE 屬性 ───────────────────────────────────────────

    /// <summary>是否啟用 AIvsAI 每方獨立 NNUE 設定。</summary>
    public bool UsePerPlayerNnue
    {
        get => usePerPlayerNnue;
        set
        {
            if (SetProperty(ref usePerPlayerNnue, value))
            {
                SaveSettings();
                if (!value)
                {
                    engineProvider.ClearPerPlayerNnue();
                    PerPlayerStatusMessage = "已停用每方獨立設定，回復使用全域引擎。";
                }
            }
        }
    }

    public NnuePlayerViewModel RedPlayer   { get; }
    public NnuePlayerViewModel BlackPlayer { get; }

    /// <summary>本機訓練面板 ViewModel（首次存取時才建立，避免預先配置記憶體）。</summary>
    /// <summary>引擎管理面板 ViewModel（在 NNUE Tab 上方顯示）。</summary>
    public LoadedEngineListViewModel LoadedEngineList { get; }

    public NnueTrainingViewModel Training  => lazyTraining.Value;

    public string PerPlayerStatusMessage
    {
        get => perPlayerStatusMessage;
        private set => SetProperty(ref perPlayerStatusMessage, value);
    }

    // ── 指令 ─────────────────────────────────────────────────────────

    public ICommand BrowseModelCommand            { get; }
    public ICommand LoadModelCommand              { get; }
    public ICommand UnloadModelCommand            { get; }
    public ICommand ApplyPerPlayerSettingsCommand { get; }

    // ── 私有邏輯（全域模型） ─────────────────────────────────────────

    private void BrowseModel()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "選取 NNUE 模型檔",
            Filter = "NNUE 模型 (*.nnue)|*.nnue|所有檔案|*.*",
        };
        if (dlg.ShowDialog() == true)
            ModelPath = dlg.FileName;
    }

    private async Task LoadModelAsync()
    {
        if (!File.Exists(ModelPath))
        {
            StatusMessage = $"檔案不存在：{ModelPath}";
            return;
        }

        isLoading = true;
        StatusMessage = "載入中…";
        OnPropertyChanged(nameof(CanLoad));

        try
        {
            await network.LoadFromFileAsync(ModelPath);
            StatusMessage = $"已載入：{Path.GetFileName(ModelPath)}";
            SaveSettings();
        }
        catch (Exception ex)
        {
            StatusMessage = $"載入失敗：{ex.Message}";
        }
        finally
        {
            isLoading = false;
            NotifyModelChanged();
        }
    }

    private async Task TryAutoLoadModelAsync()
    {
        isLoading = true;
        StatusMessage = "自動載入中…";
        OnPropertyChanged(nameof(CanLoad));

        try
        {
            await network.LoadFromFileAsync(modelPath);
            StatusMessage = $"已自動載入：{Path.GetFileName(modelPath)}";
            SaveSettings();
        }
        catch (Exception ex)
        {
            StatusMessage = $"自動載入失敗：{ex.Message}";
        }
        finally
        {
            isLoading = false;
            NotifyModelChanged();
        }
    }

    private void UnloadModel()
    {
        network.Unload();
        StatusMessage = "模型已卸載";
        NotifyModelChanged();
    }

    private void NotifyModelChanged()
    {
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(CanLoad));
        OnPropertyChanged(nameof(ModelInfo));
        OnPropertyChanged(nameof(ModelDescription));
        OnPropertyChanged(nameof(ModelFileSizeText));
        OnPropertyChanged(nameof(ModelLoadedAt));
    }

    // ── 私有邏輯（每方獨立 NNUE） ────────────────────────────────────

    private async Task ApplyPerPlayerSettingsAsync()
    {
        isApplyingPerPlayer = true;
        PerPlayerStatusMessage = "套用中…";
        OnPropertyChanged(nameof(ApplyPerPlayerSettingsCommand));

        try
        {
            var redConfig   = RedPlayer.BuildConfig();
            var blackConfig = BlackPlayer.BuildConfig();

            await engineProvider.ApplyPerPlayerNnueAsync(redConfig, blackConfig);

            var redDesc   = redConfig   != null ? Path.GetFileName(redConfig.ModelFilePath)   : "手工評估";
            var blackDesc = blackConfig != null ? Path.GetFileName(blackConfig.ModelFilePath) : "手工評估";
            PerPlayerStatusMessage = $"已套用：紅方={redDesc}，黑方={blackDesc}。下局起生效。";
            SaveSettings();
        }
        catch (Exception ex)
        {
            PerPlayerStatusMessage = $"套用失敗：{ex.Message}";
        }
        finally
        {
            isApplyingPerPlayer = false;
            OnPropertyChanged(nameof(ApplyPerPlayerSettingsCommand));
        }
    }

    private async Task AutoApplyPerPlayerAsync()
    {
        try
        {
            var redConfig   = RedPlayer.BuildConfig();
            var blackConfig = BlackPlayer.BuildConfig();
            if (redConfig == null && blackConfig == null) return;

            await engineProvider.ApplyPerPlayerNnueAsync(redConfig, blackConfig);

            var redDesc   = redConfig   != null ? Path.GetFileName(redConfig.ModelFilePath)   : "手工評估";
            var blackDesc = blackConfig != null ? Path.GetFileName(blackConfig.ModelFilePath) : "手工評估";
            PerPlayerStatusMessage = $"已自動套用：紅方={redDesc}，黑方={blackDesc}。";
        }
        catch (Exception ex)
        {
            PerPlayerStatusMessage = $"自動套用失敗：{ex.Message}";
        }
    }

    private void SaveSettings()
    {
        settingsService.SaveNnueSettings(new NnueSettings
        {
            IsEnabled        = network.IsLoaded,
            ModelFilePath    = ModelPath,
            EvaluationMode   = EvaluationMode,
            UsePerPlayerNnue = usePerPlayerNnue,
            RedPlayerSettings   = usePerPlayerNnue ? RedPlayer.ToSettings()   : null,
            BlackPlayerSettings = usePerPlayerNnue ? BlackPlayer.ToSettings() : null,
        });
    }
}
