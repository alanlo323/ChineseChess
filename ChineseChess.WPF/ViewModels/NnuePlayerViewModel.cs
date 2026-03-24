using ChineseChess.Application.Configuration;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// 單一 AI 玩家的 NNUE 設定 ViewModel（紅方或黑方）。
/// 不直接持有 INnueNetwork；模型的實際載入由 IAiEngineFactory 在套用設定時處理。
/// </summary>
public sealed class NnuePlayerViewModel : ObservableObject
{
    private string modelPath = string.Empty;
    private NnueEvaluationMode evaluationMode = NnueEvaluationMode.Composite;

    public NnuePlayerViewModel()
    {
        BrowseModelCommand = new RelayCommand(_ => BrowseModel());
    }

    // ── 公開屬性 ─────────────────────────────────────────────────────

    public string ModelPath
    {
        get => modelPath;
        set
        {
            if (SetProperty(ref modelPath, value))
                OnPropertyChanged(nameof(ModelFileExists));
        }
    }

    public NnueEvaluationMode EvaluationMode
    {
        get => evaluationMode;
        set => SetProperty(ref evaluationMode, value);
    }

    public IEnumerable<NnueEvaluationMode> EvaluationModes =>
        Enum.GetValues<NnueEvaluationMode>();

    /// <summary>模型檔案是否存在（用於 UI 提示）。</summary>
    public bool ModelFileExists => !string.IsNullOrEmpty(modelPath) && File.Exists(modelPath);

    public ICommand BrowseModelCommand { get; }

    // ── 設定建構 ─────────────────────────────────────────────────────

    /// <summary>
    /// 依目前設定建立 NnueEngineConfig。
    /// 若模式為 Disabled 或模型路徑空/不存在，回傳 null（表示使用 Handcrafted）。
    /// </summary>
    public NnueEngineConfig? BuildConfig()
    {
        if (evaluationMode == NnueEvaluationMode.Disabled)
            return null;
        if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            return null;
        return new NnueEngineConfig
        {
            ModelFilePath  = modelPath,
            EvaluationMode = evaluationMode,
        };
    }

    /// <summary>從持久化設定還原 ViewModel 狀態。</summary>
    public void LoadFrom(NnueSettings? saved)
    {
        if (saved == null) return;
        modelPath      = saved.ModelFilePath ?? string.Empty;
        evaluationMode = saved.EvaluationMode;
        OnPropertyChanged(nameof(ModelPath));
        OnPropertyChanged(nameof(EvaluationMode));
        OnPropertyChanged(nameof(ModelFileExists));
    }

    /// <summary>將目前狀態轉為持久化設定。</summary>
    public NnueSettings ToSettings() => new NnueSettings
    {
        IsEnabled      = ModelFileExists && evaluationMode != NnueEvaluationMode.Disabled,
        ModelFilePath  = modelPath,
        EvaluationMode = evaluationMode,
    };

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
}
