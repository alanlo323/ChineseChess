namespace ChineseChess.Application.Enums;

/// <summary>
/// WXF 規則下的著法分類，用於重複局面裁決。
/// 數值越大表示違規等級越高（Check > Chase > Idle）。
/// </summary>
public enum MoveClassification
{
    /// <summary>吃子 或 兵前進（不可逆，打斷重複鏈）。</summary>
    Cancel = 0,

    /// <summary>非將軍、非有效追擊的一般著法（可逆）。</summary>
    Idle = 1,

    /// <summary>追擊對方單一未受保護棋子（WXF 長捉規則）。</summary>
    Chase = 2,

    /// <summary>走完後對方被將軍（WXF 長將規則）。</summary>
    Check = 3,
}
