using ChineseChess.Application.Interfaces;
using ChineseChess.Infrastructure.AI.Nnue;
using ChineseChess.WPF.Core;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// NNUE 設定頁 ViewModel：
///   - NNUE 模型管理（LoadedNnueModelList）
///   - AIvsAI 每方獨立 NNUE 設定（UsePerPlayerNnue）
///   - 設定持久化（nnue-user-settings.json）
/// </summary>
public sealed class NnueViewModel : ObservableObject, IDisposable
{
    private readonly INnueSettingsService settingsService;
    private readonly IEngineProvider engineProvider;
    private readonly LoadedNnueModelRegistry nnueModelRegistry;
    private readonly Lazy<NnueTrainingViewModel> lazyTraining;

    private bool usePerPlayerNnue;
    private string perPlayerStatusMessage = string.Empty;
    private bool isApplyingPerPlayer;

    public NnueViewModel(
        INnueSettingsService settingsService,
        IEngineProvider engineProvider,
        Lazy<NnueTrainingViewModel> lazyTraining,
        LoadedNnueModelListViewModel loadedNnueModelList,
        LoadedNnueModelRegistry nnueModelRegistry)
    {
        this.settingsService   = settingsService;
        this.engineProvider    = engineProvider;
        this.nnueModelRegistry = nnueModelRegistry;
        this.lazyTraining      = lazyTraining;
        LoadedNnueModelList    = loadedNnueModelList ?? throw new ArgumentNullException(nameof(loadedNnueModelList));

        RedPlayer   = new NnuePlayerViewModel(nnueModelRegistry);
        BlackPlayer = new NnuePlayerViewModel(nnueModelRegistry);

        var saved = settingsService.LoadNnueSettings();
        usePerPlayerNnue = saved.UsePerPlayerNnue;
        RedPlayer.LoadFrom(saved.RedPlayerSettings);
        BlackPlayer.LoadFrom(saved.BlackPlayerSettings);

        ApplyPerPlayerSettingsCommand = new RelayCommand(_ => _ = ApplyPerPlayerSettingsAsync(), _ => !isApplyingPerPlayer);

        nnueModelRegistry.ModelsChanged += OnModelsChanged;

        if (usePerPlayerNnue)
            _ = AutoApplyPerPlayerAsync();
    }

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

    /// <summary>NNUE 模型管理面板（在 NNUE Tab 顯示）。</summary>
    public LoadedNnueModelListViewModel LoadedNnueModelList { get; }

    public NnueTrainingViewModel Training => lazyTraining.Value;

    public string PerPlayerStatusMessage
    {
        get => perPlayerStatusMessage;
        private set => SetProperty(ref perPlayerStatusMessage, value);
    }

    // ── 指令 ─────────────────────────────────────────────────────────

    public ICommand ApplyPerPlayerSettingsCommand { get; }

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

    private void OnModelsChanged()
    {
        // per-player ComboBox 的 AvailableModels 由 NnuePlayerViewModel 自行監聽 registry 更新
    }

    private void SaveSettings()
    {
        settingsService.SaveNnueSettings(new Application.Configuration.NnueSettings
        {
            UsePerPlayerNnue    = usePerPlayerNnue,
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
