namespace ChineseChess.Application.Configuration;

/// <summary>開局庫行為設定。</summary>
public class OpeningBookSettings
{
    /// <summary>是否啟用開局庫。預設 true。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>開局庫二進位檔路徑。預設 "openingbook.bin"。</summary>
    public string BookFilePath { get; set; } = "openingbook.bin";

    /// <summary>
    /// 同一局面有多個候選走法時，是否依權重隨機選擇。
    /// false 時永遠選權重最高的走法。預設 true。
    /// </summary>
    public bool UseRandomSelection { get; set; } = true;

    /// <summary>
    /// 開局庫查詢的最大半步數（ply）。
    /// 超過此步數後不再查詢開局庫，直接進入 AI 搜尋。預設 20。
    /// </summary>
    public int MaxPly { get; set; } = 20;
}
