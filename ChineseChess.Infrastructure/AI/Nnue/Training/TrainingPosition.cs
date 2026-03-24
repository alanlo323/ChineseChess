namespace ChineseChess.Infrastructure.AI.Nnue.Training;

/// <summary>
/// 單一訓練樣本：棋盤局面 + 引擎評分 + 對局結果。
/// 從 .plain 格式解析而來。
/// </summary>
public sealed class TrainingPosition
{
    /// <summary>棋盤局面（FEN 格式）。</summary>
    public string Fen { get; init; } = string.Empty;

    /// <summary>引擎靜態評分（以 centipawns 為單位，正值代表先手有利）。</summary>
    public int Score { get; init; }

    /// <summary>對局結果：1.0f = 紅勝，0.5f = 和局，0.0f = 黑勝。</summary>
    public float Result { get; init; }
}
