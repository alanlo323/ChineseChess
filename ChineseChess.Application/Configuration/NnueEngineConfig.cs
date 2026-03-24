namespace ChineseChess.Application.Configuration;

/// <summary>
/// 建立帶特定 NNUE 設定的引擎時所需的設定 DTO。
/// 此類別作為 Application 層與 Infrastructure 層之間的設定傳遞物件，
/// 避免 Application 層直接依賴 Infrastructure 的具體評估器型別。
/// </summary>
public class NnueEngineConfig
{
    /// <summary>.nnue 模型檔路徑。</summary>
    public string ModelFilePath { get; init; } = string.Empty;

    /// <summary>評估模式。</summary>
    public NnueEvaluationMode EvaluationMode { get; init; } = NnueEvaluationMode.Composite;
}
