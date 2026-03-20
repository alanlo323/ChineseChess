using ChineseChess.Application.Enums;

namespace ChineseChess.Application.Configuration;

/// <summary>
/// Pikafish 引擎的專屬進階設定。
/// 對應 Pikafish 的 UCI 選項（setoption name X value Y）。
/// 序列化至 engine-user-settings.json，向後相容（舊 JSON 缺少此物件時使用預設值）。
/// </summary>
public class PikafishSettings
{
    /// <summary>MultiPV：同時計算的最佳走法數量（1-500）。</summary>
    public int MultiPv { get; set; } = 1;

    /// <summary>技能等級（0-20）；0 最弱，20 最強。</summary>
    public int SkillLevel { get; set; } = 20;

    /// <summary>是否啟用 ELO 限制強度模式。</summary>
    public bool UciLimitStrength { get; set; } = false;

    /// <summary>ELO 目標強度（1280-3133），僅在 UciLimitStrength 為 true 時生效。</summary>
    public int UciElo { get; set; } = 2850;

    /// <summary>是否啟用六十步和棋規則。</summary>
    public bool SixtyMoveRule { get; set; } = true;

    /// <summary>六十步規則的最大半步數（1-150）。</summary>
    public int Rule60MaxPly { get; set; } = 120;

    /// <summary>將殺威脅搜尋深度（0-10）；0 表示停用。</summary>
    public int MateThreatDepth { get; set; } = 0;

    /// <summary>評分顯示方式。</summary>
    public PikafishScoreType ScoreType { get; set; } = PikafishScoreType.Elo;

    /// <summary>是否啟用 LU 輸出格式。</summary>
    public bool LuOutput { get; set; } = true;

    /// <summary>和棋規則的特殊處理方式。</summary>
    public PikafishDrawRule DrawRule { get; set; } = PikafishDrawRule.None;

    /// <summary>自訂評估神經網路檔案路徑；空字串表示使用內建。</summary>
    public string EvalFile { get; set; } = string.Empty;
}
