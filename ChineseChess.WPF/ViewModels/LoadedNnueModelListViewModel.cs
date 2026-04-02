using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// NNUE Tab 的模型管理面板 ViewModel。
/// 管理已載入 .nnue 模型的列表，並提供新增 / 移除功能。
/// </summary>
public sealed class LoadedNnueModelListViewModel : ObservableObject, IDisposable
{
    private readonly ILoadedNnueModelRegistry registry;
    private LoadedNnueModelItemViewModel? selectedModel;
    private bool isAdding;
    private string addStatusMessage = string.Empty;

    public LoadedNnueModelListViewModel(ILoadedNnueModelRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));

        Models = [];
        RefreshModelList();

        registry.ModelsChanged += OnModelsChanged;
        AddModelCommand = new AsyncRelayCommand(async _ => await AddModelAsync());
    }

    // ─── 屬性 ─────────────────────────────────────────────────────────────

    public ObservableCollection<LoadedNnueModelItemViewModel> Models { get; }

    public LoadedNnueModelItemViewModel? SelectedModel
    {
        get => selectedModel;
        set => SetProperty(ref selectedModel, value);
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

    public ICommand AddModelCommand { get; }

    // ─── 私有邏輯 ─────────────────────────────────────────────────────────

    private async Task AddModelAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title  = "選擇 NNUE 模型檔",
            Filter = "NNUE 模型 (*.nnue)|*.nnue|所有檔案|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        IsAdding = true;
        AddStatusMessage = "載入中…";

        try
        {
            var info = await registry.AddModelAsync(dialog.FileName);
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

    private void OnModelsChanged()
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(RefreshModelList);
    }

    private void RefreshModelList()
    {
        var current = registry.Models;

        var toRemove = Models
            .Where(item => !current.Any(m => m.Id == item.ModelId))
            .ToList();
        foreach (var item in toRemove)
            Models.Remove(item);

        foreach (var info in current)
        {
            var existing = Models.FirstOrDefault(item => item.ModelId == info.Id);
            if (existing != null)
                existing.UpdateFrom(info);
            else
                Models.Add(new LoadedNnueModelItemViewModel(info, registry));
        }
    }

    public void Dispose()
    {
        registry.ModelsChanged -= OnModelsChanged;
    }
}

/// <summary>單一已載入 NNUE 模型的 ViewModel（列表行）。</summary>
public sealed class LoadedNnueModelItemViewModel : ObservableObject
{
    private readonly ILoadedNnueModelRegistry registry;
    private string fileName;
    private string description;
    private string formattedFileSize;
    private DateTime loadedAt;

    public LoadedNnueModelItemViewModel(LoadedNnueModelInfo info, ILoadedNnueModelRegistry registry)
    {
        this.registry    = registry;
        ModelId          = info.Id;
        FilePath         = info.FilePath;
        fileName         = info.FileName;
        description      = info.Description;
        formattedFileSize = info.FormattedFileSize;
        loadedAt         = info.LoadedAt;

        RemoveCommand = new RelayCommand(_ => registry.RemoveModel(ModelId));
    }

    // ─── 屬性 ─────────────────────────────────────────────────────────────

    public string ModelId          { get; }
    public string FilePath         { get; private set; }

    public string FileName
    {
        get => fileName;
        private set => SetProperty(ref fileName, value);
    }

    public string Description
    {
        get => description;
        private set => SetProperty(ref description, value);
    }

    public string FormattedFileSize
    {
        get => formattedFileSize;
        private set => SetProperty(ref formattedFileSize, value);
    }

    public DateTime LoadedAt
    {
        get => loadedAt;
        private set => SetProperty(ref loadedAt, value);
    }

    public string DisplayName => registry.GetModelInfo(ModelId)?.DisplayName ?? FileName;

    public bool IsWeightsLoaded => registry.IsModelLoaded(ModelId);

    // ─── 命令 ─────────────────────────────────────────────────────────────

    public ICommand RemoveCommand { get; }

    // ─── 公開輔助 ─────────────────────────────────────────────────────────

    public void UpdateFrom(LoadedNnueModelInfo info)
    {
        FilePath          = info.FilePath;
        FileName          = info.FileName;
        Description       = info.Description;
        FormattedFileSize = info.FormattedFileSize;
        LoadedAt          = info.LoadedAt;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IsWeightsLoaded));
    }
}
