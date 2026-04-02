using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Infrastructure.AI.Nnue;
using ChineseChess.Infrastructure.AI.Nnue.Network;
using ChineseChess.WPF.Core;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// NNUE 設定頁 ViewModel：
///   - 全域模型從已載入列表以 ComboBox 選取（取代舊版路徑 TextBox + 載入按鈕）
///   - 顯示模型元數據（路徑、大小、描述）
///   - 評估模式切換
///   - AIvsAI 每方獨立 NNUE 設定（UsePerPlayerNnue）
///   - 外部引擎管理（LoadedEngineList）
///   - NNUE 模型管理（LoadedNnueModelList）
///   - 設定持久化（nnue-user-settings.json）
/// </summary>
public sealed class NnueViewModel : ObservableObject, IDisposable
{
    private readonly INnueNetwork network;
    private readonly INnueSettingsService settingsService;
    private readonly IEngineProvider engineProvider;
    private readonly LoadedNnueModelRegistry nnueModelRegistry;
    private readonly Lazy<NnueTrainingViewModel> lazyTraining;

    private string? selectedGlobalModelId;
    private string statusMessage = "尚未載入模型";
    private NnueEvaluationMode evaluationMode = NnueEvaluationMode.Composite;
    private bool usePerPlayerNnue;
    private string perPlayerStatusMessage = string.Empty;
    private bool isApplyingPerPlayer;

    public NnueViewModel(
        INnueNetwork network,
        INnueSettingsService settingsService,
        IEngineProvider engineProvider,
        Lazy<NnueTrainingViewModel> lazyTraining,
        LoadedEngineListViewModel loadedEngineList,
        LoadedNnueModelListViewModel loadedNnueModelList,
        LoadedNnueModelRegistry nnueModelRegistry)
    {
        this.network           = network;
        this.settingsService   = settingsService;
        this.engineProvider    = engineProvider;
        this.nnueModelRegistry = nnueModelRegistry;
        this.lazyTraining      = lazyTraining;
        LoadedEngineList     = loadedEngineList    ?? throw new ArgumentNullException(nameof(loadedEngineList));
        LoadedNnueModelList  = loadedNnueModelList ?? throw new ArgumentNullException(nameof(loadedNnueModelList));

        RedPlayer   = new NnuePlayerViewModel(nnueModelRegistry);
        BlackPlayer = new NnuePlayerViewModel(nnueModelRegistry);

        var saved = settingsService.LoadNnueSettings();
        evaluationMode       = saved.EvaluationMode;
        usePerPlayerNnue     = saved.UsePerPlayerNnue;
        selectedGlobalModelId = saved.SelectedModelId;
        RedPlayer.LoadFrom(saved.RedPlayerSettings);
        BlackPlayer.LoadFrom(saved.BlackPlayerSettings);

        UnloadModelCommand            = new RelayCommand(_ => UnloadModel(), _ => network.IsLoaded);
        ApplyPerPlayerSettingsCommand = new RelayCommand(_ => _ = ApplyPerPlayerSettingsAsync(), _ => !isApplyingPerPlayer);

        nnueModelRegistry.ModelsChanged += OnModelsChanged;

        // 若上次已選取模型，啟動時自動套用至全域 network
        if (!string.IsNullOrEmpty(selectedGlobalModelId)
            && evaluationMode != NnueEvaluationMode.Disabled)
        {
            _ = TryAutoLoadModelAsync();
        }

        if (usePerPlayerNnue)
            _ = AutoApplyPerPlayerAsync();
    }

    // ── 全域 NNUE 屬性 ───────────────────────────────────────────────

    /// <summary>可選的已載入模型列表（全域 ComboBox 資料來源）。</summary>
    public IReadOnlyList<LoadedNnueModelInfo> AvailableModels => nnueModelRegistry.Models;

    public bool HasNoModels => nnueModelRegistry.Models.Count == 0;

    /// <summary>全域模型選取（ComboBox SelectedValue）。選取時立即套用至全域 INnueNetwork。</summary>
    public string? SelectedGlobalModelId
    {
        get => selectedGlobalModelId;
        set
        {
            if (SetProperty(ref selectedGlobalModelId, value))
            {
                _ = ApplyGlobalModelAsync(value);
                SaveSettings();
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsLoaded  => network.IsLoaded;

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

    /// <summary>外部引擎管理面板（在 NNUE Tab 顯示）。</summary>
    public LoadedEngineListViewModel LoadedEngineList { get; }

    /// <summary>NNUE 模型管理面板（在 NNUE Tab 顯示）。</summary>
    public LoadedNnueModelListViewModel LoadedNnueModelList { get; }

    public NnueTrainingViewModel Training => lazyTraining.Value;

    public string PerPlayerStatusMessage
    {
        get => perPlayerStatusMessage;
        private set => SetProperty(ref perPlayerStatusMessage, value);
    }

    // ── 指令 ─────────────────────────────────────────────────────────

    public ICommand UnloadModelCommand            { get; }
    public ICommand ApplyPerPlayerSettingsCommand { get; }

    // ── 私有邏輯（全域模型） ─────────────────────────────────────────

    private async Task ApplyGlobalModelAsync(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId))
        {
            network.Unload();
            StatusMessage = "模型已清除";
            NotifyModelChanged();
            return;
        }

        // 優先使用已快取的 weights（不重新讀檔）
        var weights  = nnueModelRegistry.GetWeights(modelId);
        var modelData = nnueModelRegistry.GetModelInfo(modelId);

        if (weights != null && modelData != null)
        {
            network.LoadFromWeights(weights, new NnueModelInfo
            {
                FilePath      = modelData.FilePath,
                Description   = modelData.Description,
                FileSizeBytes = modelData.FileSizeBytes,
                LoadedAt      = modelData.LoadedAt,
            });
            StatusMessage = $"已套用：{modelData.DisplayName}";
        }
        else if (modelData != null)
        {
            // weights 尚未載入完成（registry 仍在背景載入中），改從檔案載入
            StatusMessage = "載入中…";
            try
            {
                await network.LoadFromFileAsync(modelData.FilePath);
                StatusMessage = $"已載入：{modelData.DisplayName}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"載入失敗：{ex.Message}";
            }
        }
        else
        {
            StatusMessage = "找不到選取的模型";
        }

        NotifyModelChanged();
    }

    private async Task TryAutoLoadModelAsync()
    {
        // 等待 registry 背景載入完成（先檢查再延遲，快速載入時立即返回）
        for (int retry = 0; retry < 5; retry++)
        {
            if (!string.IsNullOrEmpty(selectedGlobalModelId)
                && nnueModelRegistry.IsModelLoaded(selectedGlobalModelId))
            {
                await ApplyGlobalModelAsync(selectedGlobalModelId);
                return;
            }
            if (retry < 4) await Task.Delay(500);
        }
        // 最後嘗試：即使 weights 未就緒也嘗試套用（會退化到讀檔）
        if (!string.IsNullOrEmpty(selectedGlobalModelId))
            await ApplyGlobalModelAsync(selectedGlobalModelId);
    }

    private void UnloadModel()
    {
        network.Unload();
        selectedGlobalModelId = null;
        OnPropertyChanged(nameof(SelectedGlobalModelId));
        StatusMessage = "模型已卸載";
        SaveSettings();
        NotifyModelChanged();
    }

    private void NotifyModelChanged()
    {
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(ModelInfo));
        OnPropertyChanged(nameof(ModelDescription));
        OnPropertyChanged(nameof(ModelFileSizeText));
        OnPropertyChanged(nameof(ModelLoadedAt));
    }

    private void OnModelsChanged()
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // 若選取的全域模型已被移除
            if (selectedGlobalModelId != null
                && nnueModelRegistry.GetModelInfo(selectedGlobalModelId) == null)
            {
                selectedGlobalModelId = null;
                network.Unload();
                StatusMessage = "已選模型已被移除";
                NotifyModelChanged();
                OnPropertyChanged(nameof(SelectedGlobalModelId));
                SaveSettings();
            }

            OnPropertyChanged(nameof(AvailableModels));
            OnPropertyChanged(nameof(HasNoModels));
        });
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

            var redDesc   = redConfig   != null
                ? (nnueModelRegistry.GetModelInfo(redConfig.ModelId ?? string.Empty)?.DisplayName ?? redConfig.ModelFilePath)
                : "手工評估";
            var blackDesc = blackConfig != null
                ? (nnueModelRegistry.GetModelInfo(blackConfig.ModelId ?? string.Empty)?.DisplayName ?? blackConfig.ModelFilePath)
                : "手工評估";
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
            PerPlayerStatusMessage = "已自動套用每方獨立設定。";
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
            SelectedModelId  = selectedGlobalModelId,
            ModelFilePath    = network.ModelInfo?.FilePath ?? string.Empty,
            EvaluationMode   = EvaluationMode,
            UsePerPlayerNnue = usePerPlayerNnue,
            RedPlayerSettings   = usePerPlayerNnue ? RedPlayer.ToSettings()   : null,
            BlackPlayerSettings = usePerPlayerNnue ? BlackPlayer.ToSettings() : null,
        });
    }

    public void Dispose()
    {
        nnueModelRegistry.ModelsChanged -= OnModelsChanged;
        RedPlayer.Dispose();
        BlackPlayer.Dispose();
    }
}
