using ChineseChess.Application.Configuration;

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

    // ── 每方獨立 NNUE 設定 ────────────────────────────────────────────────

    /// <summary>
    /// 套用每方獨立的 NNUE 設定，為各方建立帶獨立 NnueNetwork 的引擎（非同步，因為需要載入模型）。
    /// 傳入 null 表示該方使用 Handcrafted 評估器。
    /// 優先順序：外部引擎 > 每方 NNUE 引擎 > 全域內建引擎。
    /// </summary>
    Task ApplyPerPlayerNnueAsync(
        NnueEngineConfig? redConfig,
        NnueEngineConfig? blackConfig,
        CancellationToken ct = default);

    /// <summary>清除每方獨立 NNUE 設定，回復使用內建（全域）引擎。</summary>
    void ClearPerPlayerNnue();

    /// <summary>是否已套用每方獨立 NNUE 設定。</summary>
    bool HasPerPlayerNnue { get; }
}
