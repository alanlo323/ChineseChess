using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Tests.Application;

/// <summary>
/// EngineProvider 單元測試。
/// 驗證引擎切換、狀態旗標、Dispose 呼叫行為。
/// </summary>
public class EngineProviderTests
{
    // ─── 測試替身 ─────────────────────────────────────────────────────────

    private sealed class FakeEngine : IAiEngine, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;

        public Task<SearchResult> SearchAsync(IBoard board, SearchSettings settings, CancellationToken ct = default, IProgress<SearchProgress>? progress = null)
            => Task.FromResult(new SearchResult());
        public Task<IReadOnlyList<MoveEvaluation>> EvaluateMovesAsync(IBoard board, IEnumerable<Move> moves, int depth, CancellationToken ct = default, IProgress<string>? progress = null)
            => Task.FromResult<IReadOnlyList<MoveEvaluation>>([]);
        public Task ExportTranspositionTableAsync(Stream output, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public Task ImportTranspositionTableAsync(Stream input, bool asJson, CancellationToken ct = default) => Task.CompletedTask;
        public TTStatistics GetTTStatistics() => new TTStatistics();
        public IAiEngine CloneWithCopiedTT() => new FakeEngine();
        public IAiEngine CloneWithEmptyTT()  => new FakeEngine();
        public void MergeTranspositionTableFrom(IAiEngine other) { }
        public IEnumerable<TTEntry> EnumerateTTEntries() => [];
        public TTTreeNode? ExploreTTTree(IBoard board, int maxDepth = 6) => null;
    }

    // ─── 基本行為 ─────────────────────────────────────────────────────────

    [Fact]
    public void GetRedEngine_WithNoExternal_ReturnsBuiltin()
    {
        var builtin = new FakeEngine();
        var provider = new EngineProvider(builtin);

        Assert.Same(builtin, provider.GetRedEngine());
    }

    [Fact]
    public void GetBlackEngine_WithNoExternal_ReturnsBuiltin()
    {
        var builtin = new FakeEngine();
        var provider = new EngineProvider(builtin);

        Assert.Same(builtin, provider.GetBlackEngine());
    }

    // ─── 設定外部引擎 ─────────────────────────────────────────────────────

    [Fact]
    public void SetRedExternalEngine_ShouldReturnExternalEngine()
    {
        var builtin  = new FakeEngine();
        var external = new FakeEngine();
        var provider = new EngineProvider(builtin);

        provider.SetRedExternalEngine(external);

        Assert.Same(external, provider.GetRedEngine());
        Assert.Same(builtin,  provider.GetBlackEngine()); // 黑方不受影響
    }

    [Fact]
    public void SetBlackExternalEngine_ShouldReturnExternalEngine()
    {
        var builtin  = new FakeEngine();
        var external = new FakeEngine();
        var provider = new EngineProvider(builtin);

        provider.SetBlackExternalEngine(external);

        Assert.Same(external, provider.GetBlackEngine());
        Assert.Same(builtin,  provider.GetRedEngine()); // 紅方不受影響
    }

    // ─── 恢復內建引擎 ─────────────────────────────────────────────────────

    [Fact]
    public void SetRedExternalEngine_ToNull_RestoresBuiltin()
    {
        var builtin  = new FakeEngine();
        var external = new FakeEngine();
        var provider = new EngineProvider(builtin);

        provider.SetRedExternalEngine(external);
        provider.SetRedExternalEngine(null);

        Assert.Same(builtin, provider.GetRedEngine());
    }

    // ─── IsExternal 狀態旗標 ──────────────────────────────────────────────

    [Fact]
    public void IsRedExternal_ReflectsExternalEngineState()
    {
        var builtin  = new FakeEngine();
        var external = new FakeEngine();
        var provider = new EngineProvider(builtin);

        Assert.False(provider.IsRedExternal);
        provider.SetRedExternalEngine(external);
        Assert.True(provider.IsRedExternal);
        provider.SetRedExternalEngine(null);
        Assert.False(provider.IsRedExternal);
    }

    [Fact]
    public void IsBlackExternal_ReflectsExternalEngineState()
    {
        var builtin  = new FakeEngine();
        var external = new FakeEngine();
        var provider = new EngineProvider(builtin);

        Assert.False(provider.IsBlackExternal);
        provider.SetBlackExternalEngine(external);
        Assert.True(provider.IsBlackExternal);
        provider.SetBlackExternalEngine(null);
        Assert.False(provider.IsBlackExternal);
    }

    // ─── 替換時舊引擎被 Dispose ───────────────────────────────────────────

    [Fact]
    public void SetRedExternalEngine_WhenReplacing_DisposesOldEngine()
    {
        var builtin  = new FakeEngine();
        var first    = new FakeEngine();
        var second   = new FakeEngine();
        var provider = new EngineProvider(builtin);

        provider.SetRedExternalEngine(first);
        provider.SetRedExternalEngine(second);

        Assert.True(first.Disposed,   "舊引擎應被 Dispose");
        Assert.False(second.Disposed, "新引擎不應被 Dispose");
    }

    [Fact]
    public void SetBlackExternalEngine_WhenReplacing_DisposesOldEngine()
    {
        var builtin  = new FakeEngine();
        var first    = new FakeEngine();
        var second   = new FakeEngine();
        var provider = new EngineProvider(builtin);

        provider.SetBlackExternalEngine(first);
        provider.SetBlackExternalEngine(second);

        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
    }
}
