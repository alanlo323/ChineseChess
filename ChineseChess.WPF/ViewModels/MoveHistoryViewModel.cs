using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Enums;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>棋譜側邊欄 ViewModel，管理走法歷史顯示、重播控制、匯出匯入。</summary>
public class MoveHistoryViewModel : ObservableObject, IDisposable
{
    private readonly IGameService gameService;
    private readonly IGameRecordService recordService;

    private string stepIndicator = "0 / 0";
    private bool isReplaying;
    private bool isEloMode;
    private GameMode selectedContinueMode = GameMode.PlayerVsAi;

    public ObservableCollection<MoveStepViewModel> Steps { get; } = new();

    /// <summary>當前步改變時觸發，View 訂閱後執行滾動。</summary>
    public event Action? ScrollToCurrent;

    public string StepIndicator
    {
        get => stepIndicator;
        private set => SetProperty(ref stepIndicator, value);
    }

    public bool IsReplaying
    {
        get => isReplaying;
        private set
        {
            if (SetProperty(ref isReplaying, value))
            {
                OnPropertyChanged(nameof(IsLive));
            }
        }
    }

    public bool IsLive => !isReplaying;

    public GameMode SelectedContinueMode
    {
        get => selectedContinueMode;
        set => SetProperty(ref selectedContinueMode, value);
    }

    public GameMode[] AvailableModes { get; } = Enum.GetValues<GameMode>();

    public ICommand StepForwardCommand { get; }
    public ICommand StepBackCommand { get; }
    public ICommand GoToStartCommand { get; }
    public ICommand GoToEndCommand { get; }
    public ICommand EnterReplayCommand { get; }
    public ICommand NavigateToStepCommand { get; }
    public ICommand ContinueFromHereCommand { get; }
    public ICommand ExportGameCommand { get; }
    public ICommand ImportGameCommand { get; }

    public MoveHistoryViewModel(IGameService gameService, IGameRecordService recordService)
    {
        this.gameService = gameService;
        this.recordService = recordService;

        StepForwardCommand = new RelayCommand(
            async _ => await gameService.StepForwardAsync(),
            _ => isReplaying && gameService.ReplayCurrentStep < gameService.MoveHistory.Count);

        StepBackCommand = new RelayCommand(
            async _ => await gameService.StepBackAsync(),
            _ => isReplaying && gameService.ReplayCurrentStep > 0);

        GoToStartCommand = new RelayCommand(
            async _ => await gameService.GoToStartAsync(),
            _ => isReplaying);

        GoToEndCommand = new RelayCommand(
            async _ => await gameService.GoToEndAsync());

        EnterReplayCommand = new RelayCommand(
            async _ => await gameService.EnterReplayModeAsync(),
            _ => !isReplaying);

        NavigateToStepCommand = new RelayCommand(
            async param =>
            {
                if (param is int step)
                    await gameService.NavigateToAsync(step);
                else if (param is MoveStepViewModel vm)
                    await gameService.NavigateToAsync(vm.StepNumber);
            });

        ContinueFromHereCommand = new RelayCommand(
            async _ => await gameService.ContinueFromCurrentPositionAsync(selectedContinueMode),
            _ => isReplaying);

        ExportGameCommand = new RelayCommand(async _ => await ExportGameAsync());
        ImportGameCommand = new RelayCommand(async _ => await ImportGameAsync());

        gameService.MoveHistoryChanged += OnMoveHistoryChanged;
        gameService.ReplayStateChanged += OnReplayStateChanged;
    }

    private void OnMoveHistoryChanged()
    {
        if (isEloMode) return; // Elo 模式下不覆蓋棋譜顯示
        var app = System.Windows.Application.Current;
        if (app != null)
            app.Dispatcher.InvokeAsync(RefreshSteps);
        else
            RefreshSteps();
    }

    /// <summary>切換至 Elo 評估模式：清空棋譜，暫停遊戲歷史同步。</summary>
    public void StartEloMode()
    {
        isEloMode = true;
        var app = System.Windows.Application.Current;
        if (app != null)
            app.Dispatcher.InvokeAsync(() => { Steps.Clear(); StepIndicator = "0 / 0"; });
        else
        { Steps.Clear(); StepIndicator = "0 / 0"; }
    }

    /// <summary>新增一步 Elo 走法記錄（在 UI 執行緒呼叫）。</summary>
    public void AddEloMove(string notation, PieceColor turn, int stepNumber)
    {
        var vm = new MoveStepViewModel
        {
            StepNumber = stepNumber,
            Notation = notation,
            TurnLabel = turn == PieceColor.Red ? "紅" : "黑",
            IsCurrent = true
        };

        var app = System.Windows.Application.Current;
        if (app != null)
            app.Dispatcher.InvokeAsync(() =>
            {
                if (Steps.Count > 0) Steps[Steps.Count - 1].IsCurrent = false;
                Steps.Add(vm);
                StepIndicator = $"{stepNumber} 步";
                ScrollToCurrent?.Invoke();
            });
        else
        {
            if (Steps.Count > 0) Steps[Steps.Count - 1].IsCurrent = false;
            Steps.Add(vm);
            StepIndicator = $"{stepNumber} 步";
        }
    }

    /// <summary>結束 Elo 模式，恢復顯示正常遊戲棋譜。</summary>
    public void StopEloMode()
    {
        isEloMode = false;
        OnMoveHistoryChanged();
    }

    private void OnReplayStateChanged()
    {
        var app = System.Windows.Application.Current;
        if (app != null)
            app.Dispatcher.InvokeAsync(RefreshReplayState);
        else
            RefreshReplayState();
    }

    private void RefreshSteps()
    {
        var history = gameService.MoveHistory;
        var currentStep = gameService.ReplayCurrentStep;

        // 若步數相同，只更新高亮，不重建集合（避免清單滾動跳動）
        if (Steps.Count == history.Count)
        {
            for (int i = 0; i < Steps.Count; i++)
                Steps[i].IsCurrent = (i + 1) == currentStep;
        }
        else
        {
            Steps.Clear();
            foreach (var entry in history)
            {
                Steps.Add(new MoveStepViewModel
                {
                    StepNumber = entry.StepNumber,
                    Notation   = entry.Notation,
                    TurnLabel  = entry.Turn == PieceColor.Red ? "紅" : "黑",
                    IsCurrent  = entry.StepNumber == currentStep,
                });
            }
        }

        StepIndicator = $"{currentStep} / {history.Count}";
        ScrollToCurrent?.Invoke();

        // 通知 CanExecute 重新評估
        RefreshCommandState();
    }

    private void RefreshReplayState()
    {
        IsReplaying = gameService.ReplayState == ReplayState.Replaying;

        // 同步當前步高亮
        var currentStep = gameService.ReplayCurrentStep;
        for (int i = 0; i < Steps.Count; i++)
            Steps[i].IsCurrent = (i + 1) == currentStep;

        StepIndicator = $"{currentStep} / {gameService.MoveHistory.Count}";
        ScrollToCurrent?.Invoke();
        RefreshCommandState();
    }

    private void RefreshCommandState()
    {
        // CommandManager.RequerySuggested 已在 RelayCommand 中自動連結，
        // 呼叫 InvalidateRequerySuggested 強制所有 CanExecute 立即重新評估
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private async Task ExportGameAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title  = "匯出棋局",
            Filter = "Chinese Chess Game|*.ccgame|All files|*.*",
            FileName = $"game-{DateTime.Now:yyyyMMdd_HHmmss}.ccgame",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var record = gameService.ExportGameRecord();
            await recordService.ExportAsync(record, dialog.FileName);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"匯出失敗：{ex.Message}", "錯誤",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task ImportGameAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title           = "匯入棋局",
            Filter          = "Chinese Chess Game|*.ccgame|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var record = await recordService.ImportAsync(dialog.FileName);
            await gameService.LoadGameRecordAsync(record);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"匯入失敗：{ex.Message}", "錯誤",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public void Dispose()
    {
        gameService.MoveHistoryChanged -= OnMoveHistoryChanged;
        gameService.ReplayStateChanged -= OnReplayStateChanged;
    }
}
