namespace ChineseChess.Application.Configuration;

/// <summary>
/// 建立帶特定 NNUE 設定的引擎時所需的設定 DTO。
/// 此類別作為 Application 層與 Infrastructure 層之間的設定傳遞物件，
/// 避免 Application 層直接依賴 Infrastructure 的具體評估器型別。
/// </summary>
public class NnueEngineConfig
{
    /// <summary>.nnue 模型檔路徑（直接指定路徑；ModelId 優先）。</summary>
    public string ModelFilePath { get; init; } = string.Empty;

    /// <summary>從已載入模型 Registry 選取的模型 ID（優先使用，可共享記憶體中的權重）。</summary>
    public string? ModelId { get; init; }

    /// <summary>評估模式。</summary>
    public NnueEvaluationMode EvaluationMode { get; init; } = NnueEvaluationMode.Composite;
}
