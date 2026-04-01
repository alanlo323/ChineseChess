namespace ChineseChess.Application.Enums;

/// <summary>
/// AI 玩家使用的引擎類型。
/// </summary>
public enum AiEngineType
{
    /// <summary>內部引擎（SearchEngine + Handcrafted/NNUE 評估器）。</summary>
    Internal,

    /// <summary>外部引擎（透過 UCI/UCCI 協議通訊）。</summary>
    External
}
