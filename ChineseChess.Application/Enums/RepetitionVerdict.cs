namespace ChineseChess.Application.Enums;

/// <summary>
/// WXF 重複局面裁決結果。
/// </summary>
public enum RepetitionVerdict
{
    /// <summary>未達重複局面條件，或重複鏈被吃子/兵前進打斷。</summary>
    None,

    /// <summary>雙方同等級違規（或均為 Idle）→ 和局。</summary>
    Draw,

    /// <summary>黑方違規等級較重（長將/長捉）→ 紅方勝。</summary>
    RedWins,

    /// <summary>紅方違規等級較重（長將/長捉）→ 黑方勝。</summary>
    BlackWins,
}
