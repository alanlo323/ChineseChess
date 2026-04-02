using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.WPF.Core;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// 單一 AI 玩家的 NNUE 設定 ViewModel（紅方或黑方）。
/// 改為從已載入模型列表（ILoadedNnueModelRegistry）以 ComboBox 選取，
/// 不再直接輸入檔案路徑。
/// </summary>
public sealed class NnuePlayerViewModel : ObservableObject
{
    private readonly ILoadedNnueModelRegistry registry;
    private string? selectedModelId;
    private NnueEvaluationMode evaluationMode = NnueEvaluationMode.Composite;

    public NnuePlayerViewModel(ILoadedNnueModelRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        registry.ModelsChanged += OnModelsChanged;
    }

    // ── 公開屬性 ─────────────────────────────────────────────────────

    /// <summary>可選的已載入模型列表（由 Registry 提供）。</summary>
    public IReadOnlyList<LoadedNnueModelInfo> AvailableModels => registry.Models;

    public bool HasNoModels => registry.Models.Count == 0;

    /// <summary>目前選取的模型 ID（綁定 ComboBox SelectedValue）。</summary>
    public string? SelectedModelId
    {
        get => selectedModelId;
        set => SetProperty(ref selectedModelId, value);
    }

    public NnueEvaluationMode EvaluationMode
    {
        get => evaluationMode;
        set => SetProperty(ref evaluationMode, value);
    }

    public IEnumerable<NnueEvaluationMode> EvaluationModes =>
        Enum.GetValues<NnueEvaluationMode>();

    // ── 設定建構 ─────────────────────────────────────────────────────

    /// <summary>
    /// 依目前設定建立 NnueEngineConfig。
    /// 若模式為 Disabled 或未選取模型，回傳 null（表示使用 Handcrafted）。
    /// </summary>
    public NnueEngineConfig? BuildConfig()
    {
        if (evaluationMode == NnueEvaluationMode.Disabled)
            return null;
        if (string.IsNullOrEmpty(selectedModelId))
            return null;
        var info = registry.GetModelInfo(selectedModelId);
        if (info == null) return null;

        return new NnueEngineConfig
        {
            ModelId        = selectedModelId,
            ModelFilePath  = info.FilePath,
            EvaluationMode = evaluationMode,
        };
    }

    /// <summary>從持久化設定還原 ViewModel 狀態。</summary>
    public void LoadFrom(NnueSettings? saved)
    {
        if (saved == null) return;
        evaluationMode  = saved.EvaluationMode;
        selectedModelId = saved.SelectedModelId;

        // 向後相容：舊版只有路徑，嘗試在 Registry 中比對
        if (selectedModelId == null && !string.IsNullOrEmpty(saved.ModelFilePath))
        {
            selectedModelId = registry.Models
                .FirstOrDefault(m => string.Equals(
                    m.FilePath, saved.ModelFilePath, StringComparison.OrdinalIgnoreCase))
                ?.Id;
        }

        OnPropertyChanged(nameof(SelectedModelId));
        OnPropertyChanged(nameof(EvaluationMode));
    }

    /// <summary>將目前狀態轉為持久化設定。</summary>
    public NnueSettings ToSettings()
    {
        var info = selectedModelId != null ? registry.GetModelInfo(selectedModelId) : null;
        return new NnueSettings
        {
            IsEnabled       = selectedModelId != null && evaluationMode != NnueEvaluationMode.Disabled,
            ModelFilePath   = info?.FilePath ?? string.Empty,
            SelectedModelId = selectedModelId,
            EvaluationMode  = evaluationMode,
        };
    }

    public void Dispose()
    {
        registry.ModelsChanged -= OnModelsChanged;
    }

    // ── 私有邏輯 ─────────────────────────────────────────────────────

    private void OnModelsChanged()
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // 若選取的模型已被移除，自動清除
            if (selectedModelId != null && registry.GetModelInfo(selectedModelId) == null)
                SelectedModelId = null;

            OnPropertyChanged(nameof(AvailableModels));
            OnPropertyChanged(nameof(HasNoModels));
        });
    }
}
