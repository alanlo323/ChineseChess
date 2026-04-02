using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// NNUE Tab 的引擎管理面板 ViewModel。
/// 管理已載入引擎的列表顯示，並提供新增 / 重載 / 卸載功能。
/// </summary>
public sealed class LoadedEngineListViewModel : ObservableObject, IDisposable
{
    private readonly ILoadedEngineRegistry registry;
    private LoadedEngineItemViewModel? selectedEngine;
    private bool isAdding;
    private string addStatusMessage = string.Empty;

    public LoadedEngineListViewModel(ILoadedEngineRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));

        Engines = [];
        RefreshEngineList();

        registry.EnginesChanged += OnEnginesChanged;

        AddEngineCommand = new AsyncRelayCommand(async _ => await AddEngineAsync());
    }

    // ─── 屬性 ─────────────────────────────────────────────────────────────

    public ObservableCollection<LoadedEngineItemViewModel> Engines { get; }

    public LoadedEngineItemViewModel? SelectedEngine
    {
        get => selectedEngine;
        set => SetProperty(ref selectedEngine, value);
    }

    public bool IsAdding
    {
        get => isAdding;
        private set => SetProperty(ref isAdding, value);
    }

    public string AddStatusMessage
    {
        get => addStatusMessage;
        private set => SetProperty(ref addStatusMessage, value);
    }

    // ─── 命令 ─────────────────────────────────────────────────────────────

    public ICommand AddEngineCommand { get; }

    // ─── 私有邏輯 ─────────────────────────────────────────────────────────

    private async Task AddEngineAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title  = "選擇引擎執行檔",
            Filter = "引擎執行檔 (*.exe)|*.exe|所有檔案|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        IsAdding = true;
        AddStatusMessage = "自動偵測協議中（UCCI / UCI）…";

        try
        {
            var info = await registry.AddEngineAsync(dialog.FileName);
            AddStatusMessage = $"已載入：{info.DisplayName}";
        }
        catch (Exception ex)
        {
            AddStatusMessage = $"載入失敗：{ex.Message}";
        }
        finally
        {
            IsAdding = false;
        }
    }

    private void OnEnginesChanged()
    {
        // EnginesChanged 可能由背景執行緒（AutoConnectAllAsync）觸發
        // 必須 dispatch 至 UI 執行緒才能安全修改 ObservableCollection
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshEngineList);
    }

    private void RefreshEngineList()
    {
        // 在 UI 執行緒上執行（EnginesChanged 由 ViewModel 方法觸發，通常已在 UI 執行緒）
        var current = registry.Engines;

        // 移除已不存在的 item
        var toRemove = Engines
            .Where(item => !current.Any(e => e.Id == item.EngineId))
            .ToList();
        foreach (var item in toRemove)
            Engines.Remove(item);

        // 新增或更新
        foreach (var info in current)
        {
            var existing = Engines.FirstOrDefault(item => item.EngineId == info.Id);
            if (existing != null)
                existing.UpdateFrom(info, registry.GetActiveAdapter(info.Id) != null);
            else
                Engines.Add(new LoadedEngineItemViewModel(info, registry.GetActiveAdapter(info.Id) != null, registry));
        }
    }

    public void Dispose()
    {
        registry.EnginesChanged -= OnEnginesChanged;
    }
}

/// <summary>
/// 單一已載入引擎的 ViewModel（列表行）。
/// </summary>
public sealed class LoadedEngineItemViewModel : ObservableObject
{
    private readonly ILoadedEngineRegistry registry;
    private string statusText;
    private bool isLoading;

    public LoadedEngineItemViewModel(LoadedEngineInfo info, bool isConnected, ILoadedEngineRegistry registry)
    {
        this.registry = registry;

        EngineId       = info.Id;
        EngineName     = info.EngineName;
        EngineAuthor   = info.EngineAuthor;
        Protocol       = info.Protocol.ToString();
        EloRating      = info.EloRating;
        ExecutablePath = info.ExecutablePath;
        DiscoveredAt   = info.DiscoveredAt;
        statusText     = isConnected ? "已連線" : "未連線";

        ReloadCommand = new AsyncRelayCommand(async _ => await ReloadAsync(), _ => !isLoading);
        RemoveCommand = new RelayCommand(_ => registry.RemoveEngine(EngineId), _ => !isLoading);
    }

    // ─── 屬性 ─────────────────────────────────────────────────────────────

    public string EngineId       { get; }
    public string EngineName     { get; private set; }
    public string EngineAuthor   { get; private set; }
    public string Protocol       { get; private set; }
    public int? EloRating        { get; private set; }
    public string ExecutablePath { get; private set; }
    public DateTime DiscoveredAt { get; private set; }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    public string EloDisplay => EloRating.HasValue ? $"ELO {EloRating}" : "ELO 未知";

    // ─── 命令 ─────────────────────────────────────────────────────────────

    public ICommand ReloadCommand { get; }
    public ICommand RemoveCommand { get; }

    // ─── 公開輔助 ─────────────────────────────────────────────────────────

    public void UpdateFrom(LoadedEngineInfo info, bool isConnected)
    {
        EngineName     = info.EngineName;
        EngineAuthor   = info.EngineAuthor;
        Protocol       = info.Protocol.ToString();
        EloRating      = info.EloRating;
        ExecutablePath = info.ExecutablePath;
        DiscoveredAt   = info.DiscoveredAt;
        StatusText     = isConnected ? "已連線" : "未連線";
        OnPropertyChanged(string.Empty);
    }

    // ─── 私有 ─────────────────────────────────────────────────────────────

    private async Task ReloadAsync()
    {
        IsLoading = true;
        StatusText = "重新載入中…";
        try
        {
            await registry.ReloadEngineAsync(EngineId);
            StatusText = "已連線";
        }
        catch (Exception ex)
        {
            StatusText = $"連線失敗：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
