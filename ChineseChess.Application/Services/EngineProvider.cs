using ChineseChess.Application.Interfaces;
using System;

namespace ChineseChess.Application.Services;

/// <summary>
/// IEngineProvider 實作。
/// 持有內建引擎參照，並允許為紅方 / 黑方分別設定外部引擎替代內建引擎。
/// </summary>
public sealed class EngineProvider : IEngineProvider
{
    private readonly IAiEngine builtinEngine;
    private IAiEngine? redExternal;
    private IAiEngine? blackExternal;

    public EngineProvider(IAiEngine builtinEngine)
    {
        this.builtinEngine = builtinEngine ?? throw new ArgumentNullException(nameof(builtinEngine));
    }

    // ─── 查詢 ────────────────────────────────────────────────────────────

    public IAiEngine GetRedEngine()   => redExternal   ?? builtinEngine;
    public IAiEngine GetBlackEngine() => blackExternal ?? builtinEngine;

    public bool IsRedExternal   => redExternal   != null;
    public bool IsBlackExternal => blackExternal != null;

    // ─── 設定 ────────────────────────────────────────────────────────────

    public void SetRedExternalEngine(IAiEngine? engine)
    {
        DisposeIfDifferent(redExternal, engine);
        redExternal = engine;
    }

    public void SetBlackExternalEngine(IAiEngine? engine)
    {
        DisposeIfDifferent(blackExternal, engine);
        blackExternal = engine;
    }

    // ─── 私有輔助 ────────────────────────────────────────────────────────

    /// <summary>若新引擎與舊引擎不同（且舊引擎實作 IDisposable），先 Dispose 舊引擎。</summary>
    private static void DisposeIfDifferent(IAiEngine? old, IAiEngine? next)
    {
        if (old != null && !ReferenceEquals(old, next) && old is IDisposable disposable)
            disposable.Dispose();
    }
}
