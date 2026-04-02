using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using System.Diagnostics;

namespace ChineseChess.Infrastructure.AI.Protocol;

/// <summary>
/// 已載入引擎的登錄表實作（Infrastructure 層）。
/// 負責引擎生命週期：新增、重載、移除，以及持久化。
///
/// 執行緒安全：以 lock(enginesLock) 保護 engines 字典，
/// 因為 AutoConnectAllAsync 背景工作與 UI 執行緒的 Add/Remove 會並發存取。
/// </summary>
public sealed class LoadedEngineRegistry : ILoadedEngineRegistry
{
    private readonly ILoadedEngineListSettingsService settingsService;
    private readonly IEngineProvider engineProvider;

    // engineId → (info, adapter?)
    private readonly Dictionary<string, (LoadedEngineInfo info, ExternalEngineAdapter? adapter)> engines = [];
    private readonly object enginesLock = new();
    private bool disposed;

    public event Action? EnginesChanged;

    public LoadedEngineRegistry(
        ILoadedEngineListSettingsService settingsService,
        IEngineProvider engineProvider)
    {
        this.settingsService = settingsService;
        this.engineProvider = engineProvider;

        // 啟動時從設定檔恢復列表（僅靜態 info，adapter = null，待背景自動連接）
        var saved = settingsService.LoadSettings();
        lock (enginesLock)
        {
            foreach (var info in saved.Engines)
                engines[info.Id] = (info, null);
        }

        // 背景自動連接
        _ = AutoConnectAllAsync();
    }

    public IReadOnlyList<LoadedEngineInfo> Engines
    {
        get
        {
            lock (enginesLock)
                return engines.Values.Select(v => v.info).ToList();
        }
    }

    /// <summary>
    /// 載入引擎：自動偵測協議（UCCI 優先），取得 id name / author / ELO，
    /// 加入列表並持久化。
    /// </summary>
    public async Task<LoadedEngineInfo> AddEngineAsync(string executablePath, CancellationToken ct = default)
    {
        var adapter = await ExternalEngineAdapter.DetectAndConnectAsync(executablePath, ct);

        var info = new LoadedEngineInfo
        {
            ExecutablePath = executablePath,
            EngineName     = adapter.EngineName,
            EngineAuthor   = adapter.EngineAuthor,
            Protocol       = adapter.DetectedProtocol,
            EloRating      = adapter.EloRating,
            DiscoveredAt   = DateTime.Now,
        };

        lock (enginesLock)
            engines[info.Id] = (info, adapter);

        PersistEngines();
        EnginesChanged?.Invoke();
        return info;
    }

    /// <summary>重新啟動引擎 process，更新 EngineName / ELO。</summary>
    public async Task ReloadEngineAsync(string engineId, CancellationToken ct = default)
    {
        LoadedEngineInfo? oldInfo;
        ExternalEngineAdapter? oldAdapter;

        lock (enginesLock)
        {
            if (!engines.TryGetValue(engineId, out var entry))
                throw new KeyNotFoundException($"引擎 ID 不存在：{engineId}");
            (oldInfo, oldAdapter) = entry;
        }

        // 先清除 EngineProvider 中可能正在使用的舊 adapter，再 Dispose
        ClearFromEngineProvider(engineId, oldAdapter);
        oldAdapter?.Dispose();

        var newAdapter = await ExternalEngineAdapter.DetectAndConnectAsync(oldInfo.ExecutablePath, ct);

        var newInfo = oldInfo with
        {
            EngineName   = newAdapter.EngineName,
            EngineAuthor = newAdapter.EngineAuthor,
            Protocol     = newAdapter.DetectedProtocol,
            EloRating    = newAdapter.EloRating,
        };

        lock (enginesLock)
            engines[engineId] = (newInfo, newAdapter);

        PersistEngines();
        EnginesChanged?.Invoke();
    }

    /// <summary>卸載引擎：結束 process，從列表移除，更新持久化。</summary>
    public void RemoveEngine(string engineId)
    {
        ExternalEngineAdapter? adapter;

        lock (enginesLock)
        {
            if (!engines.TryGetValue(engineId, out var entry))
                return;
            adapter = entry.adapter;
            engines.Remove(engineId);
        }

        // 先從 EngineProvider 清除，再 Dispose，避免 use-after-dispose
        ClearFromEngineProvider(engineId, adapter);
        adapter?.Dispose();

        PersistEngines();
        EnginesChanged?.Invoke();
    }

    public LoadedEngineInfo? GetEngineInfo(string engineId)
    {
        lock (enginesLock)
            return engines.TryGetValue(engineId, out var entry) ? entry.info : null;
    }

    public IExternalEngineAdapter? GetActiveAdapter(string engineId)
    {
        lock (enginesLock)
            return engines.TryGetValue(engineId, out var entry) ? entry.adapter : null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        List<ExternalEngineAdapter?> adapters;
        lock (enginesLock)
        {
            adapters = engines.Values.Select(v => v.adapter).ToList();
            engines.Clear();
        }

        foreach (var adapter in adapters)
        {
            try { adapter?.Dispose(); }
            catch { /* 忽略退出時的錯誤 */ }
        }
    }

    // ─── 私有輔助 ─────────────────────────────────────────────────────────

    /// <summary>若 EngineProvider 目前使用的是該 adapter，先設為 null 再 Dispose。</summary>
    private void ClearFromEngineProvider(string engineId, ExternalEngineAdapter? adapter)
    {
        if (adapter == null) return;
        try
        {
            // 比對目前 provider 的引擎是否是同一個 adapter 實例
            if (engineProvider.IsRedExternal)
            {
                var current = engineProvider.GetRedEngine();
                if (ReferenceEquals(current, adapter))
                    engineProvider.SetRedExternalEngine(null);
            }
            if (engineProvider.IsBlackExternal)
            {
                var current = engineProvider.GetBlackEngine();
                if (ReferenceEquals(current, adapter))
                    engineProvider.SetBlackExternalEngine(null);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"清除 EngineProvider 引擎時發生錯誤：{ex.Message}");
        }
    }

    private void PersistEngines()
    {
        List<LoadedEngineInfo> snapshot;
        lock (enginesLock)
            snapshot = engines.Values.Select(v => v.info).ToList();

        settingsService.SaveSettings(new LoadedEngineListSettings { Engines = snapshot });
    }

    private async Task AutoConnectAllAsync()
    {
        List<(string id, LoadedEngineInfo info)> toConnect;
        lock (enginesLock)
            toConnect = engines
                .Where(kv => kv.Value.adapter == null)
                .Select(kv => (kv.Key, kv.Value.info))
                .ToList();

        foreach (var (id, info) in toConnect)
        {
            try
            {
                var adapter = new ExternalEngineAdapter(info.ExecutablePath, info.Protocol);
                await adapter.InitializeAsync();
                lock (enginesLock)
                {
                    // 確認此 id 仍在列表中（可能已被移除）
                    if (engines.ContainsKey(id))
                        engines[id] = (info, adapter);
                    else
                        adapter.Dispose();
                }
                EnginesChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"自動連接引擎失敗（{info.EngineName}）：{ex.Message}");
                // 保留 info，adapter 維持 null，下次手動重載可重試
            }
        }
    }
}
