namespace ChineseChess.Application.Interfaces;

/// <summary>
/// 可切換引擎抽象介面。
/// 管理紅方 / 黑方所使用的 AI 引擎（內建或第三方外部引擎）。
/// </summary>
public interface IEngineProvider
{
    /// <summary>取得紅方目前使用的引擎。</summary>
    IAiEngine GetRedEngine();

    /// <summary>取得黑方目前使用的引擎。</summary>
    IAiEngine GetBlackEngine();

    /// <summary>
    /// 設定紅方外部引擎；傳入 <c>null</c> 表示恢復使用內建引擎。
    /// 若已有舊的外部引擎，呼叫此方法時會先 Dispose 舊引擎。
    /// </summary>
    void SetRedExternalEngine(IAiEngine? engine);

    /// <summary>
    /// 設定黑方外部引擎；傳入 <c>null</c> 表示恢復使用內建引擎。
    /// 若已有舊的外部引擎，呼叫此方法時會先 Dispose 舊引擎。
    /// </summary>
    void SetBlackExternalEngine(IAiEngine? engine);

    /// <summary>紅方目前是否使用外部引擎。</summary>
    bool IsRedExternal { get; }

    /// <summary>黑方目前是否使用外部引擎。</summary>
    bool IsBlackExternal { get; }
}
