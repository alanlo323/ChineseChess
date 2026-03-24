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
///   - 模型載入 / 卸載
///   - 顯示模型元數據（路徑、大小、描述）
///   - 評估模式切換
///   - 設定持久化
/// </summary>
public sealed class NnueViewModel : ObservableObject
{
    private readonly INnueNetwork network;
    private readonly INnueSettingsService settingsService;

    private string modelPath  = string.Empty;
    private string statusMessage = "尚未載入模型";
    private bool isLoading;
    private NnueEvaluationMode evaluationMode = NnueEvaluationMode.Composite;

    public NnueViewModel(INnueNetwork network, INnueSettingsService settingsService)
    {
        this.network         = network;
        this.settingsService = settingsService;

        // 載入已儲存設定
        var saved = settingsService.LoadNnueSettings();
        modelPath      = saved.ModelFilePath;
        evaluationMode = saved.EvaluationMode;

        BrowseModelCommand = new RelayCommand(_ => BrowseModel());
        LoadModelCommand   = new RelayCommand(_ => _ = LoadModelAsync(), _ => !isLoading && File.Exists(ModelPath));
        UnloadModelCommand = new RelayCommand(_ => UnloadModel(), _ => network.IsLoaded);
    }

    // ── 公開屬性 ─────────────────────────────────────────────────────

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

    // ── 指令 ─────────────────────────────────────────────────────────

    public ICommand BrowseModelCommand { get; }
    public ICommand LoadModelCommand   { get; }
    public ICommand UnloadModelCommand { get; }

    // ── 私有邏輯 ─────────────────────────────────────────────────────

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

    private void SaveSettings()
    {
        settingsService.SaveNnueSettings(new NnueSettings
        {
            IsEnabled      = network.IsLoaded,
            ModelFilePath  = ModelPath,
            EvaluationMode = EvaluationMode,
        });
    }
}
