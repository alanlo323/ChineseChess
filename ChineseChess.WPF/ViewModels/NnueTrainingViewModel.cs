using ChineseChess.Infrastructure.AI.Nnue.Training;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using WpfApp = System.Windows.Application;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// NNUE 本機訓練面板 ViewModel。
///
/// 生命週期：
///   - StartCommand → 建立 TrainingNetwork + NnueTrainer → 非同步訓練迴圈
///   - StopCommand  → trainer.Stop()（僅送出取消訊號）
///   - 訓練迴圈的 continuation（await trainer.StartAsync() 之後）負責清理
///   - ExportNowCommand → 中途將當前網路匯出至 OutputModelPath
///
/// 注意：TrainingNetwork 約佔 ~200MB，僅在訓練期間存活；
///       trainNet / trainer 均在 UI 執行緒設置，多數存取亦在 UI 執行緒，
///       ExportNow 需捕捉本地引用防止並行清理。
/// </summary>
public sealed class NnueTrainingViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 200;

    // ── 設定 ──────────────────────────────────────────────────────────

    private string trainingDataPath = string.Empty;
    private string outputModelPath  = string.Empty;
    private string learningRateText = "0.001";
    private int    batchSize        = 256;
    private int    epochCount       = 20;

    // ── 進度顯示 ──────────────────────────────────────────────────────

    private int    currentEpoch    = 0;
    private int    totalEpochs     = 0;
    private float  currentLoss     = 0f;
    private float  bestLoss        = float.MaxValue;
    private double progressPercent = 0;
    private string etaText         = "—";
    private string statusMessage   = "尚未開始訓練";
    private bool   isRunning       = false;
    private bool   isPaused        = false;

    // ── 內部訓練物件（on-demand 建立；僅在 UI 執行緒設置/清除）──────

    private TrainingNetwork? trainNet;
    private NnueTrainer?     trainer;

    public NnueTrainingViewModel()
    {
        LogLines = new ObservableCollection<string>();

        BrowseDataFileCommand   = new RelayCommand(_ => BrowseDataFile());
        BrowseOutputFileCommand = new RelayCommand(_ => BrowseOutputFile());
        StartCommand            = new RelayCommand(_ => _ = StartTrainingAsync(),
                                                  _ => !IsRunning && CanStart());
        PauseResumeCommand      = new RelayCommand(_ => TogglePause(),
                                                  _ => IsRunning);
        StopCommand             = new RelayCommand(_ => StopTraining(),
                                                  _ => IsRunning);
        ExportNowCommand        = new RelayCommand(_ => ExportNow(),
                                                  _ => trainNet != null);
    }

    // ── 設定屬性 ──────────────────────────────────────────────────────

    public string TrainingDataPath
    {
        get => trainingDataPath;
        set => SetProperty(ref trainingDataPath, value);
    }

    public string OutputModelPath
    {
        get => outputModelPath;
        set => SetProperty(ref outputModelPath, value);
    }

    public string LearningRateText
    {
        get => learningRateText;
        set => SetProperty(ref learningRateText, value);
    }

    public int BatchSize
    {
        get => batchSize;
        set => SetProperty(ref batchSize, value);
    }

    public int EpochCount
    {
        get => epochCount;
        set
        {
            if (SetProperty(ref epochCount, value))
                totalEpochs = value;
        }
    }

    // ── 進度屬性（唯讀）──────────────────────────────────────────────

    public int CurrentEpoch
    {
        get => currentEpoch;
        private set => SetProperty(ref currentEpoch, value);
    }

    public int TotalEpochs
    {
        get => totalEpochs;
        private set => SetProperty(ref totalEpochs, value);
    }

    public float CurrentLoss
    {
        get => currentLoss;
        private set => SetProperty(ref currentLoss, value);
    }

    public float BestLoss
    {
        get => bestLoss;
        private set => SetProperty(ref bestLoss, value);
    }

    public double ProgressPercent
    {
        get => progressPercent;
        private set => SetProperty(ref progressPercent, value);
    }

    public string EtaText
    {
        get => etaText;
        private set => SetProperty(ref etaText, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsRunning
    {
        get => isRunning;
        private set
        {
            if (SetProperty(ref isRunning, value))
                OnPropertyChanged(nameof(PauseResumeLabel));
        }
    }

    public bool IsPaused
    {
        get => isPaused;
        private set
        {
            if (SetProperty(ref isPaused, value))
                OnPropertyChanged(nameof(PauseResumeLabel));
        }
    }

    /// <summary>暫停/繼續按鈕的標籤文字。</summary>
    public string PauseResumeLabel => (IsRunning && !IsPaused) ? "暫停" : "繼續";

    // ── 日誌 ──────────────────────────────────────────────────────────

    public ObservableCollection<string> LogLines { get; }

    // ── 指令 ──────────────────────────────────────────────────────────

    public ICommand BrowseDataFileCommand   { get; }
    public ICommand BrowseOutputFileCommand { get; }
    public ICommand StartCommand            { get; }
    public ICommand PauseResumeCommand      { get; }
    public ICommand StopCommand             { get; }
    public ICommand ExportNowCommand        { get; }

    // ── 私有：檔案瀏覽 ────────────────────────────────────────────────

    private void BrowseDataFile()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "選取訓練資料檔",
            Filter = "Plain 訓練資料 (*.plain)|*.plain|文字檔 (*.txt)|*.txt|所有檔案|*.*",
        };
        if (dlg.ShowDialog() == true)
            TrainingDataPath = dlg.FileName;
    }

    private void BrowseOutputFile()
    {
        var dlg = new SaveFileDialog
        {
            Title            = "選取輸出模型路徑",
            Filter           = "NNUE 模型 (*.nnue)|*.nnue",
            DefaultExt       = ".nnue",
            FileName         = "trained.nnue",
            OverwritePrompt  = true,
        };
        if (dlg.ShowDialog() == true)
            OutputModelPath = dlg.FileName;
    }

    // ── 私有：訓練控制 ────────────────────────────────────────────────

    private bool CanStart()
    {
        // 使用 InvariantCulture 確保不同地區設定的小數點格式（如德文逗號）不影響解析
        return File.Exists(TrainingDataPath)
            && !string.IsNullOrWhiteSpace(OutputModelPath)
            && float.TryParse(LearningRateText,
                              NumberStyles.Float,
                              CultureInfo.InvariantCulture,
                              out float lr)
            && lr > 0f;
    }

    private async Task StartTrainingAsync()
    {
        if (!CanStart()) return;

        float.TryParse(LearningRateText,
                       NumberStyles.Float,
                       CultureInfo.InvariantCulture,
                       out float lr);
        if (lr <= 0f) lr = 1e-3f;

        trainNet = new TrainingNetwork();
        var loader = new TrainingDataLoader(TrainingDataPath);

        TotalEpochs     = EpochCount;
        CurrentEpoch    = 0;
        CurrentLoss     = 0f;
        BestLoss        = float.MaxValue;
        ProgressPercent = 0;
        EtaText         = "—";
        LogLines.Clear();

        // 最佳模型存檔回調（由 NnueTrainer 背景執行緒呼叫）
        var outputPath = OutputModelPath;
        void BestModelCallback(TrainingNetwork net)
        {
            try
            {
                NnueModelExporter.Export(net, outputPath,
                    $"ChineseChess 本機訓練模型 {DateTime.Now:yyyy-MM-dd HH:mm}");
                AppendLog($"★ 已存檔最佳模型 → {Path.GetFileName(outputPath)}");
            }
            catch (Exception ex)
            {
                AppendLog($"存檔失敗：{ex.Message}");
            }
        }

        trainer = new NnueTrainer(
            trainNet,
            loader,
            progressCallback: OnProgress,
            bestModelCallback: BestModelCallback,
            learningRate: lr,
            batchSize: BatchSize,
            epochCount: EpochCount);

        IsRunning = true;
        IsPaused  = false;
        StatusMessage = "訓練中…";
        AppendLog($"訓練開始：資料={Path.GetFileName(TrainingDataPath)}，LR={lr}，Batch={BatchSize}，Epoch={EpochCount}");

        await trainer.StartAsync();

        // 訓練迴圈已結束（正常完成、被停止或發生錯誤）
        // 注意：若 OnProgress 的 IsRunning=false 回調已先執行，DisposeTrainer 仍為冪等安全
        IsRunning = false;
        IsPaused  = false;
        DisposeTrainer();
    }

    private void TogglePause()
    {
        var currentTrainer = trainer;
        if (currentTrainer == null) return;
        if (!IsPaused)
        {
            currentTrainer.Pause();
            IsPaused = true;
            StatusMessage = "已暫停";
            AppendLog("訓練已暫停。");
        }
        else
        {
            currentTrainer.Resume();
            IsPaused = false;
            StatusMessage = "訓練中（已恢復）…";
            AppendLog("訓練已恢復。");
        }
    }

    private void StopTraining()
    {
        // 僅送出取消訊號；IsRunning 由 StartTrainingAsync 的 continuation 清除，
        // 避免在背景任務尚未結束前提早開放 Start 按鈕，導致雙訓練實例。
        var currentTrainer = trainer;
        if (currentTrainer == null) return;
        currentTrainer.Stop();
        StatusMessage = "停止中…";
        AppendLog("正在停止訓練，請稍候。");
    }

    private void ExportNow()
    {
        // 捕捉本地引用，防止在 null-check 到 Export 之間被 DisposeTrainer 清除
        var net = trainNet;
        if (net == null) return;
        if (string.IsNullOrWhiteSpace(OutputModelPath)) return;
        try
        {
            NnueModelExporter.Export(net, OutputModelPath,
                $"ChineseChess 手動匯出 {DateTime.Now:yyyy-MM-dd HH:mm}");
            AppendLog($"已手動匯出：{Path.GetFileName(OutputModelPath)}");
        }
        catch (Exception ex)
        {
            AppendLog($"手動匯出失敗：{ex.Message}");
        }
    }

    // ── 私有：進度回調（由背景執行緒呼叫，Dispatch 回 UI）────────────

    private void OnProgress(TrainingProgress progress)
    {
        WpfApp.Current?.Dispatcher.InvokeAsync(() =>
        {
            CurrentEpoch    = progress.Epoch;
            CurrentLoss     = progress.Loss;
            BestLoss        = progress.BestLoss < float.MaxValue ? progress.BestLoss : BestLoss;
            ProgressPercent = TotalEpochs > 0
                ? (double)progress.Epoch / TotalEpochs * 100.0
                : 0;
            EtaText = FormatEta(progress.EtaSeconds);

            if (progress.Message != null)
            {
                StatusMessage = progress.Message;
                AppendLog(progress.Message);
            }

            // IsRunning=false 由 StartTrainingAsync continuation 統一處理，此處不重複清理
        });
    }

    // ── 私有：工具方法 ────────────────────────────────────────────────

    private static string FormatEta(double etaSeconds)
    {
        if (etaSeconds < 0) return "—";
        var ts = TimeSpan.FromSeconds(etaSeconds);
        return ts.TotalHours >= 1
            ? $"約 {(int)ts.TotalHours}h {ts.Minutes:D2}m"
            : ts.TotalMinutes >= 1
                ? $"約 {ts.Minutes}m {ts.Seconds:D2}s"
                : $"約 {ts.Seconds}s";
    }

    private void AppendLog(string line)
    {
        if (WpfApp.Current?.Dispatcher.CheckAccess() == false)
        {
            WpfApp.Current.Dispatcher.InvokeAsync(() => AppendLog(line));
            return;
        }

        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(0);
    }

    /// <summary>釋放訓練物件（冪等：多次呼叫安全）。</summary>
    private void DisposeTrainer()
    {
        trainer?.Dispose();
        trainer  = null;
        trainNet = null;
        OnPropertyChanged(nameof(ExportNowCommand));
    }

    public void Dispose()
    {
        // 確保訓練已停止後再清理（避免背景任務在 ViewModel 被 GC 後存取 disposed 物件）
        if (isRunning)
            trainer?.Stop();
        DisposeTrainer();
    }
}
