namespace ChineseChess.Application.Configuration;

/// <summary>
/// 已載入引擎列表的持久化設定，對應 loaded-engines.json。
/// </summary>
public class LoadedEngineListSettings
{
    public List<LoadedEngineInfo> Engines { get; set; } = [];
}
