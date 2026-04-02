using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Infrastructure.AI.Nnue.Network;
using System.Diagnostics;

namespace ChineseChess.Infrastructure.AI.Nnue;

/// <summary>
/// 已載入 NNUE 模型的登錄表實作（Infrastructure 層）。
///
/// 雙層快取設計：
///   models       — modelId → LoadedNnueModelInfo（元數據）
///   weightsCache — canonical filePath → NnueWeights（權重物件，相同路徑只載入一次）
///
/// 執行緒安全：以 lock(modelsLock) 保護兩個字典。
/// </summary>
public sealed class LoadedNnueModelRegistry : ILoadedNnueModelRegistry, IDisposable
{
    private readonly ILoadedNnueModelListSettingsService settingsService;

    private readonly Dictionary<string, LoadedNnueModelInfo> models = [];
    // NnueWeights 是不可變的 init-only 物件，多個 NnueNetwork 可安全共享同一實例
    private readonly Dictionary<string, NnueWeights> weightsCache = [];
    private readonly object modelsLock = new();
    private bool disposed;

    public event Action? ModelsChanged;

    public LoadedNnueModelRegistry(ILoadedNnueModelListSettingsService settingsService)
    {
        this.settingsService = settingsService;

        var saved = settingsService.LoadSettings();
        lock (modelsLock)
        {
            // 規範化路徑於載入時完成，後續操作無需再呼叫 Path.GetFullPath
            foreach (var info in saved.Models)
                models[info.Id] = info with { FilePath = Path.GetFullPath(info.FilePath) };
        }

        // 背景預載所有已儲存模型的權重
        _ = AutoLoadAllAsync().ContinueWith(
            t => Trace.TraceError($"AutoLoadAllAsync 發生未預期錯誤：{t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public IReadOnlyList<LoadedNnueModelInfo> Models
    {
        get
        {
            lock (modelsLock)
                return models.Values.ToList();
        }
    }

    /// <summary>載入 .nnue 模型檔，讀取描述與大小，加入列表並持久化。</summary>
    public async Task<LoadedNnueModelInfo> AddModelAsync(string filePath, CancellationToken ct = default)
    {
        var canonical = Path.GetFullPath(filePath);

        // 防止路徑遍歷：僅接受 .nnue 副檔名（OpenFileDialog 過濾器無法保護持久化路徑）
        if (!canonical.EndsWith(".nnue", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"僅接受 .nnue 檔案，拒絕：{Path.GetFileName(canonical)}");

        lock (modelsLock)
        {
            if (models.Values.Any(m => string.Equals(
                    m.FilePath, canonical, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"模型已載入：{Path.GetFileName(filePath)}");
        }

        var fileInfo = new FileInfo(canonical);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"找不到 .nnue 檔案：{canonical}");

        // 在 Task.Run 內載入，避免阻塞 UI 執行緒
        var weights = await Task.Run(() => NnueFileFormat.LoadWeights(canonical), ct).ConfigureAwait(false);

        var info = new LoadedNnueModelInfo
        {
            FilePath      = canonical,
            FileName      = Path.GetFileName(canonical),
            Description   = weights.Description,
            FileSizeBytes = fileInfo.Length,
        };

        lock (modelsLock)
        {
            models[info.Id] = info;
            weightsCache[canonical] = weights;
        }

        _ = PersistModelsAsync();
        ModelsChanged?.Invoke();
        return info;
    }

    /// <summary>卸載模型：從列表移除，若無其他模型共用同一檔案則釋放快取。</summary>
    public void RemoveModel(string modelId)
    {
        lock (modelsLock)
        {
            if (!models.TryGetValue(modelId, out var info))
                return;

            models.Remove(modelId);

            // 路徑已於存入時規範化，無需再呼叫 Path.GetFullPath
            bool stillUsed = models.Values.Any(m =>
                string.Equals(m.FilePath, info.FilePath, StringComparison.OrdinalIgnoreCase));
            if (!stillUsed)
                weightsCache.Remove(info.FilePath);
        }

        _ = PersistModelsAsync();
        ModelsChanged?.Invoke();
    }

    public LoadedNnueModelInfo? GetModelInfo(string modelId)
    {
        lock (modelsLock)
            return models.GetValueOrDefault(modelId);
    }

    public bool IsModelLoaded(string modelId)
    {
        lock (modelsLock)
        {
            if (!models.TryGetValue(modelId, out var info)) return false;
            return weightsCache.ContainsKey(info.FilePath);
        }
    }

    /// <summary>
    /// 取得指定模型的 NnueWeights（供 NnueAiEngineFactory 使用）。
    /// 兩個類別同在 Infrastructure 層，可直接存取 concrete type。
    /// </summary>
    public NnueWeights? GetWeights(string modelId)
    {
        lock (modelsLock)
        {
            if (!models.TryGetValue(modelId, out var info)) return null;
            return weightsCache.GetValueOrDefault(info.FilePath);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        lock (modelsLock)
        {
            models.Clear();
            weightsCache.Clear();
        }
    }

    // ── 私有輔助 ──────────────────────────────────────────────────────────

    private Task PersistModelsAsync()
    {
        List<LoadedNnueModelInfo> snapshot;
        lock (modelsLock)
            snapshot = models.Values.ToList();

        return Task.Run(() =>
            settingsService.SaveSettings(new LoadedNnueModelListSettings { Models = snapshot }));
    }

    private async Task AutoLoadAllAsync()
    {
        List<(string id, LoadedNnueModelInfo info)> toLoad;
        lock (modelsLock)
            toLoad = models
                .Where(kv => !weightsCache.ContainsKey(kv.Value.FilePath))
                .Select(kv => (kv.Key, kv.Value))
                .ToList();

        // 並行載入所有模型（各 ~17MB 的獨立 I/O，無競爭）
        var tasks = toLoad.Select(async item =>
        {
            try
            {
                var weights = await Task.Run(
                    () => NnueFileFormat.LoadWeights(item.info.FilePath)).ConfigureAwait(false);
                lock (modelsLock)
                {
                    if (models.ContainsKey(item.id))
                        weightsCache[item.info.FilePath] = weights;
                }
                return true;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"自動載入 NNUE 模型失敗（{item.info.FileName}）：{ex.Message}");
                return false;
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        if (Array.Exists(results, r => r))
            ModelsChanged?.Invoke();
    }
}
