namespace ChineseChess.Application.Configuration;

/// <summary>
/// NNUE 評估器的使用者設定，持久化至 nnue-user-settings.json。
/// </summary>
public class NnueSettings
{
    /// <summary>是否啟用 NNUE 評估（若模型未載入則自動 fallback 至手工評估）。</summary>
    public bool IsEnabled { get; set; } = false;

    /// <summary>上次成功載入的 .nnue 模型檔路徑（空字串表示未設定）。</summary>
    public string ModelFilePath { get; set; } = string.Empty;

    /// <summary>評估模式。</summary>
    public NnueEvaluationMode EvaluationMode { get; set; } = NnueEvaluationMode.Composite;

    // ── AIvsAI 每方獨立 NNUE 設定 ─────────────────────────────────────────

    /// <summary>
    /// 是否啟用每方獨立 NNUE 設定（AIvsAI 模式）。
    /// false（預設）：紅黑方共用上方的全域設定；true：各自使用下方的獨立設定。
    /// </summary>
    public bool UsePerPlayerNnue { get; set; } = false;

    /// <summary>紅方 AI 的獨立 NNUE 設定（null 表示沿用全域設定）。</summary>
    public NnueSettings? RedPlayerSettings { get; set; } = null;

    /// <summary>黑方 AI 的獨立 NNUE 設定（null 表示沿用全域設定）。</summary>
    public NnueSettings? BlackPlayerSettings { get; set; } = null;
}

/// <summary>NNUE 評估模式。</summary>
public enum NnueEvaluationMode
{
    /// <summary>停用 NNUE，只使用手工評估。</summary>
    Disabled,

    /// <summary>混合模式：NNUE 已載入時使用 NNUE，否則 fallback 至手工評估。</summary>
    Composite,

    /// <summary>純 NNUE 模式：NNUE 未載入時拋出例外。</summary>
    PureNnue,
}
