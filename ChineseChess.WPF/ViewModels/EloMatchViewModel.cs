using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Application.Services;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// Elo 評估功能的 ViewModel。
/// 管理引擎生命週期、對弈設定、即時進度、統計數據，以及 UI 命令。
/// 以 Singleton 登記，確保評估狀態在切換 Tab 時不會遺失。
/// </summary>
public class EloMatchViewModel : ObservableObject, IDisposable
{
    // ── 依賴 ──
    private readonly IAiEngine builtinEngine;
    private readonly IAiEngineFactory engineFactory;
    private readonly IEngineProvider engineProvider;
    private readonly EloMatchService matchService;

    // ── 外部可觀察的 events / 屬性（MainViewModel、ControlPanelViewModel 訂閱）──

    /// <summary>每步走完後觸發，攜帶 FEN 及最後走法格子供主棋盤顯示。</summary>
    public event Action<string, int, int>? BoardPositionChanged;

    /// <summary>評估開始時觸發。</summary>
    public event Action? EloMatchStarted;

    /// <summary>評估結束（完成或停止）時觸發。</summary>
    public event Action? EloMatchEnded;

    /// <summary>每步走完後觸發，攜帶記法、走子方、步號供棋譜顯示。</summary>
    public event Action<string, PieceColor, int>? EloMoveRecorded;

    /// <summary>引擎 A（被評估方），評估進行中有值，結束後為 null。</summary>
    public IAiEngine? EloEngineA { get; private set; }

    /// <summary>引擎 B（參照方），評估進行中有值，結束後為 null。</summary>
    public IAiEngine? EloEngineB { get; private set; }

    /// <summary>最近一步走完後的棋盤局面，供 TT 探索使用。</summary>
    public IBoard? EloCurrentBoard { get; private set; }

    // ── 執行狀態 ──
    private CancellationTokenSource? matchCts;
    private ManualResetEventSlim pauseSignal = new(true); // 初始為 signaled（不暫停）

    // ── 設定欄位 ──
    private int totalGames = 100;
    private EloEngineType selectedEngineAType = EloEngineType.CurrentBuiltin;
    private int engineADepth = 6;
    private int engineATimeLimitSec = 3;
    private EloEngineType selectedEngineBType = EloEngineType.External;
    private int engineBDepth = 6;
    private int engineBTimeLimitSec = 3;
    private int maxMovesPerGame = 200;
    private int resignThresholdCp = 1500;

    // ── 進度欄位 ──
    private bool isRunning;
    private bool isPaused;
    private int currentGameNumber;
    private int currentMoveCount;
    private string currentFen = string.Empty;
    private string currentColorInfo = string.Empty;
    private double progressPercent;

    // ── 思考進度欄位 ──
    private string analysisText = string.Empty;

    // ── 統計欄位 ──
    private int engineAWins;
    private int engineBWins;
    private int drawCount;
    private string eloDifferenceText = "—";
    private string winRateText = "—";
    private string averageGameLengthText = "—";
    private bool isLowSampleWarning;

    // ── 建構子 ──

    public EloMatchViewModel(
        IAiEngine builtinEngine,
        IAiEngineFactory engineFactory,
        IEngineProvider engineProvider,
        EloMatchService matchService)
    {
        this.builtinEngine = builtinEngine;
        this.engineFactory = engineFactory;
        this.engineProvider = engineProvider;
        this.matchService = matchService;

        GameResults = [];

        StartMatchCommand = new AsyncRelayCommand(
            async _ => await StartMatchAsync(),
            _ => !isRunning);

        StopMatchCommand = new RelayCommand(
            _ => StopMatch(),
            _ => isRunning);

        PauseResumeCommand = new RelayCommand(
            _ => TogglePause(),
            _ => isRunning);

        ExportResultsCommand = new RelayCommand(
            _ => ExportResultsToCsv(),
            _ => GameResults.Count > 0);
    }

    // ── 設定屬性 ──

    /// <summary>總對弈局數（10–500）。</summary>
    public int TotalGames
    {
        get => totalGames;
        set => SetProperty(ref totalGames, value);
    }

    public EloEngineType SelectedEngineAType
    {
        get => selectedEngineAType;
        set => SetProperty(ref selectedEngineAType, value);
    }

    public int EngineADepth
    {
        get => engineADepth;
        set => SetProperty(ref engineADepth, value);
    }

    public int EngineATimeLimitSec
    {
        get => engineATimeLimitSec;
        set => SetProperty(ref engineATimeLimitSec, value);
    }

    public EloEngineType SelectedEngineBType
    {
        get => selectedEngineBType;
        set => SetProperty(ref selectedEngineBType, value);
    }

    public int EngineBDepth
    {
        get => engineBDepth;
        set => SetProperty(ref engineBDepth, value);
    }

    public int EngineBTimeLimitSec
    {
        get => engineBTimeLimitSec;
        set => SetProperty(ref engineBTimeLimitSec, value);
    }

    public int MaxMovesPerGame
    {
        get => maxMovesPerGame;
        set => SetProperty(ref maxMovesPerGame, value);
    }

    public int ResignThresholdCp
    {
        get => resignThresholdCp;
        set => SetProperty(ref resignThresholdCp, value);
    }

    // ── 進度屬性 ──

    public bool IsRunning
    {
        get => isRunning;
        private set
        {
            SetProperty(ref isRunning, value);
            // 觸發命令的 CanExecute 重新評估
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsPaused
    {
        get => isPaused;
        private set => SetProperty(ref isPaused, value);
    }

    public int CurrentGameNumber
    {
        get => currentGameNumber;
        private set => SetProperty(ref currentGameNumber, value);
    }

    public int CurrentMoveCount
    {
        get => currentMoveCount;
        private set => SetProperty(ref currentMoveCount, value);
    }

    public string CurrentFen
    {
        get => currentFen;
        private set => SetProperty(ref currentFen, value);
    }

    /// <summary>顯示當前局的顏色配置，例如「A 執紅 vs B 執黑」。</summary>
    public string CurrentColorInfo
    {
        get => currentColorInfo;
        private set => SetProperty(ref currentColorInfo, value);
    }

    /// <summary>整體進度（0–100）。</summary>
    public double ProgressPercent
    {
        get => progressPercent;
        private set => SetProperty(ref progressPercent, value);
    }

    /// <summary>AI 即時思考進度文字（格式與正常對局一致）。</summary>
    public string AnalysisText
    {
        get => analysisText;
        private set => SetProperty(ref analysisText, value);
    }

    // ── 統計屬性 ──

    public int EngineAWins
    {
        get => engineAWins;
        private set => SetProperty(ref engineAWins, value);
    }

    public int EngineBWins
    {
        get => engineBWins;
        private set => SetProperty(ref engineBWins, value);
    }

    public int DrawCount
    {
        get => drawCount;
        private set => SetProperty(ref drawCount, value);
    }

    /// <summary>Elo 差距顯示文字，例如「+125 (±42, 95% CI)」。</summary>
    public string EloDifferenceText
    {
        get => eloDifferenceText;
        private set => SetProperty(ref eloDifferenceText, value);
    }

    /// <summary>引擎 A 勝率文字，例如「62.5%」。</summary>
    public string WinRateText
    {
        get => winRateText;
        private set => SetProperty(ref winRateText, value);
    }

    public string AverageGameLengthText
    {
        get => averageGameLengthText;
        private set => SetProperty(ref averageGameLengthText, value);
    }

    /// <summary>樣本數不足 30 時為 true，顯示警告。</summary>
    public bool IsLowSampleWarning
    {
        get => isLowSampleWarning;
        private set => SetProperty(ref isLowSampleWarning, value);
    }

    // ── 外部引擎狀態（唯讀，供 UI 控制 External 選項可用性）──

    /// <summary>是否已在「外部引擎」Tab 設定任何外部引擎。</summary>
    public bool HasExternalEngineConfigured
        => engineProvider.IsRedExternal || engineProvider.IsBlackExternal;

    /// <summary>外部引擎名稱（如已設定）。</summary>
    public string ExternalEngineLabel
    {
        get
        {
            try
            {
                if (engineProvider.IsRedExternal)
                    return engineProvider.GetRedEngine().EngineLabel;
                if (engineProvider.IsBlackExternal)
                    return engineProvider.GetBlackEngine().EngineLabel;
            }
            catch
            {
                // 引擎尚未初始化或已釋放，回傳預設值
            }
            return "（未設定）";
        }
    }

    // ── 對局記錄 ──

    public ObservableCollection<SingleGameResult> GameResults { get; }

    // ── 命令 ──

    public ICommand StartMatchCommand { get; }
    public ICommand StopMatchCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand ExportResultsCommand { get; }

    // ── 引擎 A/B 類型顯示（供 ComboBox 使用）──

    public IEnumerable<EloEngineType> AvailableEngineTypes
        => Enum.GetValues<EloEngineType>();

    // ── 暫停/繼續切換顯示文字 ──

    public string PauseResumeLabel => isPaused ? "繼續" : "暫停";

    // ── 核心執行邏輯 ──

    private async Task StartMatchAsync()
    {
        // 重置狀態
        GameResults.Clear();
        CurrentGameNumber = 0;
        CurrentMoveCount = 0;
        CurrentFen = string.Empty;
        CurrentColorInfo = string.Empty;
        ProgressPercent = 0;
        EngineAWins = 0;
        EngineBWins = 0;
        DrawCount = 0;
        EloDifferenceText = "—";
        WinRateText = "—";
        AverageGameLengthText = "—";
        IsLowSampleWarning = false;
        AnalysisText = string.Empty;

        // 驗證：兩引擎不可同為外部引擎（會共用同一實例，TT 互相汙染）
        if (selectedEngineAType == EloEngineType.External &&
            selectedEngineBType == EloEngineType.External)
        {
            MessageBox.Show(
                "引擎 A 與引擎 B 不可同時選擇外部引擎，請選擇不同的引擎類型。",
                "設定錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 解析引擎選擇
        IAiEngine? engineA = null;
        IAiEngine? engineB = null;

        try
        {
            engineA = ResolveEngine(selectedEngineAType, "A");
            if (engineA == null) return;

            engineB = ResolveEngine(selectedEngineBType, "B");
            if (engineB == null) return;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "引擎設定錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 建立設定
        var settings = new EloMatchSettings
        {
            TotalGames = totalGames,
            EngineAType = selectedEngineAType,
            EngineADepth = engineADepth,
            EngineATimeLimitMs = engineATimeLimitSec * 1000,
            EngineBType = selectedEngineBType,
            EngineBDepth = engineBDepth,
            EngineBTimeLimitMs = engineBTimeLimitSec * 1000,
            MaxMovesPerGame = maxMovesPerGame,
            ResignThresholdCp = resignThresholdCp
        };

        matchCts = new CancellationTokenSource();
        pauseSignal.Set(); // 確保未暫停

        EloEngineA = engineA;
        EloEngineB = engineB;
        IsRunning = true;
        IsPaused = false;
        EloMatchStarted?.Invoke();

        // 進度回報（Progress<T> 自動捕捉 SynchronizationContext，回呼在 UI 執行緒執行）
        var progress = new Progress<EloMatchProgress>(OnProgressReceived);
        var thinkingProgress = new Progress<string>(text => AnalysisText = text);

        try
        {
            await matchService.RunMatchAsync(engineA, engineB, settings, matchCts.Token, progress, pauseSignal, thinkingProgress);
        }
        catch (OperationCanceledException)
        {
            // 使用者手動停止，正常結束
        }
        catch (Exception ex)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show($"評估過程發生錯誤：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            pauseSignal.Set();
            matchCts?.Dispose();
            matchCts = null;

            EloEngineA = null;
            EloEngineB = null;
            EloCurrentBoard = null;
            EloMatchEnded?.Invoke();

            // 只釋放臨時建立的引擎（非 EngineProvider 管理的外部引擎）
            if (selectedEngineAType != EloEngineType.External)
                (engineA as IDisposable)?.Dispose();
            if (selectedEngineBType != EloEngineType.External)
                (engineB as IDisposable)?.Dispose();
        }
    }

    private IAiEngine? ResolveEngine(EloEngineType type, string label)
    {
        switch (type)
        {
            case EloEngineType.Handcrafted:
                return engineFactory.CreateWithHandcrafted();

            case EloEngineType.CurrentBuiltin:
                return builtinEngine.CloneWithEmptyTT();

            case EloEngineType.External:
                if (!HasExternalEngineConfigured)
                    throw new InvalidOperationException(
                        $"引擎 {label} 設為外部引擎，但尚未在「外部引擎」Tab 設定任何外部引擎。");
                // 複用 EngineProvider 中已初始化的外部引擎（執紅方優先）
                return engineProvider.IsRedExternal
                    ? engineProvider.GetRedEngine()
                    : engineProvider.GetBlackEngine();

            default:
                return null;
        }
    }

    private void StopMatch()
    {
        matchCts?.Cancel();
        // 若處於暫停狀態，先恢復以確保搜尋能響應取消
        pauseSignal.Set();
    }

    private void TogglePause()
    {
        if (isPaused)
        {
            pauseSignal.Set();
            IsPaused = false;
        }
        else
        {
            pauseSignal.Reset();
            IsPaused = true;
        }
        OnPropertyChanged(nameof(PauseResumeLabel));
    }

    // ── 進度處理（UI 執行緒）──

    private void OnProgressReceived(EloMatchProgress p)
    {
        // Progress<T> 已在建立時捕捉 SynchronizationContext，會自動在 UI 執行緒回呼
        CurrentGameNumber = p.CurrentGameNumber;
        CurrentMoveCount = p.CurrentMoveCount;
        CurrentFen = p.CurrentFen;
        CurrentColorInfo = p.EngineAPlaysRed ? "A 執紅 vs B 執黑" : "A 執黑 vs B 執紅";
        ProgressPercent = (double)p.CurrentGameNumber / p.TotalGames * 100.0;

        // 通知主棋盤更新顯示（含最後走法高亮）
        BoardPositionChanged?.Invoke(p.CurrentFen, p.LastMoveFrom, p.LastMoveTo);

        // 更新 TT 探索用棋盤快照
        var boardSnapshot = new Board();
        boardSnapshot.ParseFen(p.CurrentFen);
        EloCurrentBoard = boardSnapshot;

        // 通知棋譜新增一步
        if (!string.IsNullOrEmpty(p.MoveNotationText))
            EloMoveRecorded?.Invoke(p.MoveNotationText, p.MovingColor, p.CurrentMoveCount);

        // 若本次回報包含完成的局，加入列表
        if (p.LastGameResult != null)
            GameResults.Add(p.LastGameResult);

        UpdateStatisticsDisplay(p.RunningStats);
    }

    private void UpdateStatisticsDisplay(EloMatchStatistics stats)
    {
        if (stats.GamesPlayed == 0) return;

        EngineAWins = stats.EngineAWins;
        EngineBWins = stats.EngineBWins;
        DrawCount = stats.Draws;

        WinRateText = $"{stats.EngineAWinRate * 100.0:F1}%";
        AverageGameLengthText = $"{stats.AverageGameLength:F0} 步";
        IsLowSampleWarning = stats.IsLowSampleWarning;

        // Elo 顯示格式："+125 (±42, 95% CI)"
        double elo = stats.EloDifference;
        double halfCi = (stats.EloConfidenceHigh - stats.EloConfidenceLow) / 2.0;
        string eloSign = elo >= 0 ? "+" : string.Empty;
        EloDifferenceText = $"{eloSign}{elo:F0} (±{halfCi:F0}, 95% CI)";
    }

    // ── CSV 匯出 ──

    private void ExportResultsToCsv()
    {
        var dialog = new SaveFileDialog
        {
            Title = "匯出 Elo 評估結果",
            Filter = "CSV 檔案|*.csv",
            FileName = $"elo_match_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("局數,A執色,結果,步數,終止原因");

            foreach (var r in GameResults)
            {
                string colorA = r.EngineAPlaysRed ? "紅" : "黑";
                string outcome = r.Outcome switch
                {
                    GameOutcome.EngineAWin => "A 勝",
                    GameOutcome.EngineBWin => "B 勝",
                    GameOutcome.Draw => "和棋",
                    _ => "—"
                };
                string reason = r.Reason switch
                {
                    TerminationReason.Checkmate => "將殺",
                    TerminationReason.Stalemate => "困斃",
                    TerminationReason.DrawByRepetition => "重複局面",
                    TerminationReason.DrawByNoCapture => "無吃子",
                    TerminationReason.DrawByInsufficientMaterial => "棋子不足",
                    TerminationReason.Resignation => "認輸",
                    TerminationReason.MaxMoves => "步數上限",
                    _ => "—"
                };
                sb.AppendLine($"{r.GameNumber},{colorA},{outcome},{r.TotalMoves},{reason}");
            }

            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show($"已匯出 {GameResults.Count} 局結果至：\n{dialog.FileName}",
                "匯出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"匯出失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── IDisposable ──

    public void Dispose()
    {
        matchCts?.Cancel();
        matchCts?.Dispose();
        pauseSignal.Dispose();
    }
}
