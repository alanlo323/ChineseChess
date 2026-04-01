namespace ChineseChess.Application.Enums;

/// <summary>
/// 內部引擎使用的評估函式類型。
/// </summary>
public enum InternalEvaluatorType
{
    /// <summary>手工評估函式（基於棋子方陣表、位置特徵等）。</summary>
    Handcrafted,

    /// <summary>NNUE 神經網路評估（需載入 .nnue 模型檔）。</summary>
    Nnue
}
