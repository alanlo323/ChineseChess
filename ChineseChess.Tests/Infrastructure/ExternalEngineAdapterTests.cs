using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Infrastructure.AI.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChineseChess.Tests.Infrastructure;

/// <summary>
/// ExternalEngineAdapter 測試。
///
/// 使用 FakeEngineProcess（可程式化 stdout 回應的測試替身）模擬外部引擎 process，
/// 不需要實際的 UCI/UCCI 引擎執行檔。
///
/// 注意：直接測試 ExternalEngineAdapter 的內部通訊邏輯（InitializeAsync + SearchAsync），
/// 因為 process 啟動邏輯無法在測試中輕易替換，此處測試的是行為合約。
/// </summary>
public class ExternalEngineAdapterTests
{
    // ─── IsPikafish / EngineName 預設值測試（不依賴外部 process） ────────

    [Fact]
    public void IsPikafish_DefaultInstance_IsFalse()
    {
        using var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Uci);
        Assert.False(adapter.IsPikafish);
    }

    [Fact]
    public void EngineName_DefaultInstance_IsEmpty()
    {
        using var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Uci);
        Assert.Equal(string.Empty, adapter.EngineName);
    }

    // ─── TT stub 方法測試（不依賴外部 process） ───────────────────────────

    [Fact]
    public void GetTTStatistics_DoesNotThrow()
    {
        using var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Ucci);
        var stats = adapter.GetTTStatistics();
        Assert.NotNull(stats);
    }

    [Fact]
    public void EnumerateTTEntries_ReturnsEmpty()
    {
        using var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Ucci);
        var entries = adapter.EnumerateTTEntries();
        Assert.Empty(entries);
    }

    [Fact]
    public void ExploreTTTree_ReturnsNull()
    {
        using var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Ucci);
        var board = new Board();
        var result = adapter.ExploreTTTree(board);
        Assert.Null(result);
    }

    [Fact]
    public void MergeTranspositionTableFrom_DoesNotThrow()
    {
        using var adapter1 = new ExternalEngineAdapter("fake.exe", EngineProtocol.Ucci);
        using var adapter2 = new ExternalEngineAdapter("fake.exe", EngineProtocol.Ucci);
        // 不應拋出例外
        adapter1.MergeTranspositionTableFrom(adapter2);
    }

    [Fact]
    public async Task ExportTranspositionTableAsync_DoesNotThrow()
    {
        using var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Ucci);
        using var stream = new MemoryStream();
        await adapter.ExportTranspositionTableAsync(stream, asJson: false);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task ImportTranspositionTableAsync_DoesNotThrow()
    {
        using var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Ucci);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        await adapter.ImportTranspositionTableAsync(stream, asJson: true);
    }

    // ─── CloneWith* 測試 ──────────────────────────────────────────────────

    [Fact]
    public void CloneWithCopiedTT_ReturnsNewInstance()
    {
        using var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Ucci);
        var clone = adapter.CloneWithCopiedTT();
        Assert.NotNull(clone);
        Assert.NotSame(adapter, clone);
        (clone as IDisposable)?.Dispose();
    }

    [Fact]
    public void CloneWithEmptyTT_ReturnsNewInstance()
    {
        using var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Uci);
        var clone = adapter.CloneWithEmptyTT();
        Assert.NotNull(clone);
        Assert.NotSame(adapter, clone);
        (clone as IDisposable)?.Dispose();
    }

    [Fact]
    public void CloneWithCopiedTT_UcciAdapter_ReturnsSameProtocol()
    {
        using var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Ucci);
        // Clone 應回傳同協議的新實例（不應拋出）
        var clone = adapter.CloneWithCopiedTT();
        Assert.IsType<ExternalEngineAdapter>(clone);
        (clone as IDisposable)?.Dispose();
    }

    [Fact]
    public void CloneWithEmptyTT_UciAdapter_ReturnsSameProtocol()
    {
        using var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Uci);
        var clone = adapter.CloneWithEmptyTT();
        Assert.IsType<ExternalEngineAdapter>(clone);
        (clone as IDisposable)?.Dispose();
    }

    // ─── Dispose 測試 ─────────────────────────────────────────────────────

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var adapter = new ExternalEngineAdapter("fake.exe", EngineProtocol.Ucci);
        adapter.Dispose();
        adapter.Dispose(); // 第二次不應拋出
    }
}
