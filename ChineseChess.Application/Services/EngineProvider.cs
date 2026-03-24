using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Application.Services;

/// <summary>
/// IEngineProvider 實作。
/// 持有內建引擎參照，並允許為紅方 / 黑方分別設定外部引擎或每方獨立 NNUE 引擎。
///
/// 引擎優先順序（由高到低）：
///   外部引擎 > 每方 NNUE 引擎 > 全域內建引擎
/// </summary>
public sealed class EngineProvider : IEngineProvider, IDisposable
{
    private readonly IAiEngine builtinEngine;
    private readonly IAiEngineFactory? engineFactory;
    private IAiEngine? redExternal;
    private IAiEngine? blackExternal;
    private IAiEngine? redNnueBuiltin;   // 每方獨立 NNUE 引擎（紅方）
    private IAiEngine? blackNnueBuiltin; // 每方獨立 NNUE 引擎（黑方）

    public EngineProvider(IAiEngine builtinEngine, IAiEngineFactory? engineFactory = null)
    {
        this.builtinEngine = builtinEngine ?? throw new ArgumentNullException(nameof(builtinEngine));
        this.engineFactory = engineFactory;
    }

    // ─── 查詢 ────────────────────────────────────────────────────────────

    public IAiEngine GetRedEngine()   => redExternal   ?? redNnueBuiltin   ?? builtinEngine;
    public IAiEngine GetBlackEngine() => blackExternal ?? blackNnueBuiltin ?? builtinEngine;

    public bool IsRedExternal   => redExternal   != null;
    public bool IsBlackExternal => blackExternal != null;

    public bool HasPerPlayerNnue => redNnueBuiltin != null || blackNnueBuiltin != null;

    // ─── 外部引擎設定 ─────────────────────────────────────────────────────

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

    // ─── 每方獨立 NNUE 引擎 ───────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task ApplyPerPlayerNnueAsync(
        NnueEngineConfig? redConfig,
        NnueEngineConfig? blackConfig,
        CancellationToken ct = default)
    {
        // 釋放舊的每方 NNUE 引擎
        DisposeIfDifferent(redNnueBuiltin, null);
        DisposeIfDifferent(blackNnueBuiltin, null);
        redNnueBuiltin   = null;
        blackNnueBuiltin = null;

        if (engineFactory == null) return;

        redNnueBuiltin = redConfig != null
            ? await engineFactory.CreateWithNnueAsync(redConfig, ct).ConfigureAwait(false)
            : engineFactory.CreateWithHandcrafted();

        blackNnueBuiltin = blackConfig != null
            ? await engineFactory.CreateWithNnueAsync(blackConfig, ct).ConfigureAwait(false)
            : engineFactory.CreateWithHandcrafted();
    }

    /// <inheritdoc/>
    public void ClearPerPlayerNnue()
    {
        DisposeIfDifferent(redNnueBuiltin, null);
        DisposeIfDifferent(blackNnueBuiltin, null);
        redNnueBuiltin   = null;
        blackNnueBuiltin = null;
    }

    // ─── IDisposable ──────────────────────────────────────────────────────

    /// <summary>釋放外部引擎與每方 NNUE 引擎資源（builtinEngine 由 DI 容器管理，不在此 Dispose）。</summary>
    public void Dispose()
    {
        (redExternal as IDisposable)?.Dispose();
        (blackExternal as IDisposable)?.Dispose();
        (redNnueBuiltin as IDisposable)?.Dispose();
        (blackNnueBuiltin as IDisposable)?.Dispose();
    }

    // ─── 私有輔助 ─────────────────────────────────────────────────────────

    /// <summary>若新引擎與舊引擎不同（且舊引擎實作 IDisposable），先 Dispose 舊引擎。</summary>
    private static void DisposeIfDifferent(IAiEngine? old, IAiEngine? next)
    {
        if (old != null && !ReferenceEquals(old, next) && old is IDisposable disposable)
            disposable.Dispose();
    }
}
