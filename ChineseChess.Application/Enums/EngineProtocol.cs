namespace ChineseChess.Application.Enums;

/// <summary>
/// 外部棋類引擎通訊協議。
/// </summary>
public enum EngineProtocol
{
    /// <summary>通用中國象棋介面（傳統象棋引擎，如 eleeye、Pikafish UCCI 模式）。</summary>
    Ucci,

    /// <summary>通用西洋棋介面加象棋變體（如 Fairy-Stockfish，需設定 UCI_Variant xiangqi）。</summary>
    Uci
}
