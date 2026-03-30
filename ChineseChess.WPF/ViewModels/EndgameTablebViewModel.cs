using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using ChineseChess.Domain.Models;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

public sealed class EndgameTablebViewModel : ObservableObject, IDisposable
{
    private readonly ITablebaseService tablebaseService;
    private readonly IGameService gameService;

    private PieceConfiguration? selectedPreset;
    private bool isGenerating;
    private double generationProgress;
    private string generationStatusText = "尚未生成殘局庫";
    private string queryResultText = "（請先生成殘局庫，再點擊「查詢當前局面」）";
    private string bestMoveText = string.Empty;
    private int winCount;
    private int lossCount;
    private int drawCount;
    private int totalCount;
    private CancellationTokenSource? generationCts;
    private bool disposed;

    public EndgameTablebViewModel(ITablebaseService tablebaseService, IGameService gameService)
    {
        this.tablebaseService = tablebaseService;
        this.gameService = gameService;

        PieceSelector = new PieceCountSelectorViewModel();

        AvailableConfigurations = PieceConfiguration.Presets;
        selectedPreset = PieceConfiguration.RookVsKing;
        PieceSelector.LoadFromPreset(PieceConfiguration.RookVsKing);

        GenerateCommand              = new AsyncRelayCommand(async _ => await GenerateAsync(),              _ => !isGenerating);
        CancelGenerationCommand      = new RelayCommand     (_ => CancelGeneration(),                      _ => isGenerating);
        QueryCurrentCommand          = new AsyncRelayCommand(async _ => await QueryCurrentPositionAsync(), _ => tablebaseService.HasTablebase && !isGenerating);
        ExportCommand                = new AsyncRelayCommand(async _ => await ExportAsync(),                _ => tablebaseService.HasTablebase && !isGenerating);
        ImportCommand                = new AsyncRelayCommand(async _ => await ImportAsync(),               _ => !isGenerating);
        GenerateFromBoardCommand     = new AsyncRelayCommand(async _ => await GenerateFromCurrentBoardAsync(), _ => !isGenerating);
        SyncToTTCommand              = new RelayCommand     (_ => SyncToTT(),                              _ => tablebaseService.HasTablebase && tablebaseService.HasBoardData && !isGenerating);
        ApplyPresetCommand           = new RelayCommand     (_ => ApplyPreset(),                           _ => selectedPreset is not null && !isGenerating);
    }

    // ── 屬性 ────────────────────────────────────────────────────────────

    public PieceCountSelectorViewModel PieceSelector { get; }

    public IReadOnlyList<PieceConfiguration> AvailableConfigurations { get; }

    public PieceConfiguration? SelectedPreset
    {
        get => selectedPreset;
        set => SetProperty(ref selectedPreset, value);
    }

    public bool IsGenerating
    {
        get => isGenerating;
        private set
        {
            if (SetProperty(ref isGenerating, value))
            {
                OnPropertyChanged(nameof(IsNotGenerating));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsNotGenerating => !isGenerating;

    public double GenerationProgress
    {
        get => generationProgress;
        private set => SetProperty(ref generationProgress, value);
    }

    public string GenerationStatusText
    {
        get => generationStatusText;
        private set => SetProperty(ref generationStatusText, value);
    }

    public string QueryResultText
    {
        get => queryResultText;
        private set => SetProperty(ref queryResultText, value);
    }

    public string BestMoveText
    {
        get => bestMoveText;
        private set
        {
            if (SetProperty(ref bestMoveText, value))
                OnPropertyChanged(nameof(HasBestMoveText));
        }
    }

    public bool HasBestMoveText => !string.IsNullOrEmpty(bestMoveText);

    public int WinCount   { get => winCount;   private set => SetProperty(ref winCount,   value); }
    public int LossCount  { get => lossCount;  private set => SetProperty(ref lossCount,  value); }
    public int DrawCount  { get => drawCount;  private set => SetProperty(ref drawCount,  value); }
    public int TotalCount { get => totalCount; private set => SetProperty(ref totalCount, value); }

    public bool HasTablebase => tablebaseService.HasTablebase;

    // ── 命令 ────────────────────────────────────────────────────────────

    public ICommand GenerateCommand             { get; }
    public ICommand CancelGenerationCommand     { get; }
    public ICommand GenerateFromBoardCommand    { get; }
    public ICommand SyncToTTCommand             { get; }
    public ICommand ApplyPresetCommand          { get; }
    public ICommand QueryCurrentCommand         { get; }
    public ICommand ExportCommand               { get; }
    public ICommand ImportCommand               { get; }

    // ── 私有方法 ─────────────────────────────────────────────────────────

    private async Task GenerateAsync()
    {
        var config = PieceSelector.BuildConfiguration();

        IsGenerating = true;
        GenerationProgress = 0;
        GenerationStatusText = $"正在生成「{config.DisplayName}」殘局庫...";
        QueryResultText = string.Empty;
        BestMoveText = string.Empty;

        generationCts = new CancellationTokenSource();
        var progress = new Progress<TablebaseGenerationProgress>(OnGenerationProgress);

        try
        {
            await tablebaseService.GenerateAsync(config, progress, generationCts.Token);
            UpdateStats();
            GenerationStatusText = $"完成！共 {totalCount:N0} 局面";
            OnPropertyChanged(nameof(HasTablebase));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (OperationCanceledException)
        {
            GenerationStatusText = "已取消";
        }
        catch (Exception ex)
        {
            GenerationStatusText = $"生成失敗：{ex.Message}";
        }
        finally
        {
            IsGenerating = false;
            generationCts?.Dispose();
            generationCts = null;
        }
    }

    private async Task GenerateFromCurrentBoardAsync()
    {
        // 先把當前棋盤的子力載入選擇器，讓 UI 顯示目前識別到的組合
        PieceSelector.LoadFromBoard(gameService.CurrentBoard);

        IsGenerating = true;
        GenerationProgress = 0;
        GenerationStatusText = "正在分析當前局面並生成殘局庫...";
        QueryResultText = string.Empty;
        BestMoveText = string.Empty;

        generationCts = new CancellationTokenSource();
        var progress = new Progress<TablebaseGenerationProgress>(OnGenerationProgress);

        try
        {
            await tablebaseService.GenerateFromBoardAsync(gameService.CurrentBoard, progress, generationCts.Token);
            UpdateStats();
            GenerationStatusText = $"完成！共 {totalCount:N0} 局面";
            OnPropertyChanged(nameof(HasTablebase));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (OperationCanceledException)
        {
            GenerationStatusText = "已取消";
        }
        catch (Exception ex)
        {
            GenerationStatusText = $"生成失敗：{ex.Message}";
        }
        finally
        {
            IsGenerating = false;
            generationCts?.Dispose();
            generationCts = null;
        }
    }

    private void SyncToTT()
    {
        try
        {
            gameService.SyncTablebaseToTranspositionTable(tablebaseService);
            GenerationStatusText = $"已同步 {totalCount:N0} 個殘局庫步法至 AI 搜尋表";
        }
        catch (Exception ex)
        {
            GenerationStatusText = $"同步失敗：{ex.Message}";
        }
    }

    private void ApplyPreset()
    {
        if (selectedPreset is null) return;
        PieceSelector.LoadFromPreset(selectedPreset);
    }

    private void OnGenerationProgress(TablebaseGenerationProgress p)
    {
        GenerationProgress = p.ProgressFraction;
        GenerationStatusText = p.Summary;
        WinCount   = p.WinCount;
        LossCount  = p.LossCount;
        DrawCount  = p.DrawCount;
        TotalCount = (int)p.TotalPositions;
    }

    private void CancelGeneration()
    {
        generationCts?.Cancel();
    }

    private Task QueryCurrentPositionAsync()
    {
        var iboard = gameService.CurrentBoard;
        var entry = tablebaseService.Query(iboard);

        if (!entry.IsResolved)
        {
            QueryResultText = "此局面不在殘局庫中（子力組合不符或局面非法）";
            BestMoveText = string.Empty;
            return Task.CompletedTask;
        }

        QueryResultText = $"結論：{entry}";

        var bestMove = tablebaseService.GetBestMove(iboard);
        BestMoveText = bestMove.HasValue
            ? $"最優著法：{MoveNotation.ToNotation(bestMove.Value, iboard)}"
            : entry.Result == TablebaseResult.Draw ? "（和棋，任意著法均可）" : string.Empty;

        return Task.CompletedTask;
    }

    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "匯出殘局庫",
            Filter = "殘局庫 FEN 檔案 (*.etb)|*.etb|文字檔案 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
            DefaultExt = "etb",
            FileName = $"tablebase_{tablebaseService.CurrentConfiguration?.DisplayName ?? "unknown"}",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            GenerationStatusText = "匯出中...";
            await tablebaseService.ExportToFileAsync(dialog.FileName);
            GenerationStatusText = $"匯出完成：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            GenerationStatusText = $"匯出失敗：{ex.Message}";
        }
    }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "匯入殘局庫",
            Filter = "殘局庫 FEN 檔案 (*.etb)|*.etb|文字檔案 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true) return;

        IsGenerating = true;
        GenerationStatusText = "匯入中...";
        try
        {
            int count = await tablebaseService.ImportFromFileAsync(dialog.FileName);
            UpdateStats();
            GenerationStatusText = $"匯入完成：{count:N0} 個局面";
            OnPropertyChanged(nameof(HasTablebase));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            GenerationStatusText = $"匯入失敗：{ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private void UpdateStats()
    {
        WinCount   = tablebaseService.WinPositions;
        LossCount  = tablebaseService.LossPositions;
        DrawCount  = tablebaseService.DrawPositions;
        TotalCount = tablebaseService.TotalPositions;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        generationCts?.Cancel();
        generationCts?.Dispose();
    }
}
