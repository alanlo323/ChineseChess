using ChineseChess.Application.Configuration;
using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Models;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

public class ControlPanelViewModel : ObservableObject, IDisposable
{
    private readonly IGameService gameService;
    private GameMode selectedMode = GameMode.PlayerVsAi;
    private string statusMessage = "Ready";
    private int searchDepth;
    private int searchThinkingTime;
    private int redSearchDepth;
    private int redSearchThinkingTime;
    private int blackSearchDepth;
    private int blackSearchThinkingTime;
    private bool useSharedTT;
    private bool copyRedTtToBlackAtStart;
    private bool isSmartHintEnabled;
    private int smartHintDepth;
    private bool isMultiPvHintEnabled;
    private int multiPvCount;
    private IReadOnlyList<MultiPvItemViewModel> multiPvItems = [];
    private MultiPvItemViewModel? selectedMultiPvItem;
    private bool isTimedModeEnabled;
    private int timedModeMinutesPerPlayer;
    private PieceColor playerColor = PieceColor.Red;
    private string redTimeDisplay = "--:--";
    private string blackTimeDisplay = "--:--";
    private System.Timers.Timer? clockDisplayTimer;
    private string hintExplanationText = "（尚未產生提示）";
    private TTStatistics ttStats = new TTStatistics();
    private TTStatistics? blackTtStats = null;
    private const int TTExploreMaxDepth = 20;   // 固定最大深度（TT 搜尋深度通常 ≤ 10，此值足以顯示全樹）
    private string ttExplorerText = "（初始化中...）";
    private System.Timers.Timer? ttExplorerTimer;
    private int ttExplorerBusy;   // Interlocked 防止重疊執行（0 = 閒置，1 = 執行中）
    private int selectedTabIndex;
    private const int HintExplanationTabIndex = 3;
    private const int TTExplorerTabIndex = 2;
    private const int GameAnalysisTabIndex = 4;

    // ─── 局面分析（AI 走子後進行）────────────────────────────
    private readonly IGameAnalysisService? gameAnalysisService;
    private string gameAnalysisDisclaimer = string.Empty;
    private bool isGameAnalysisEnabled;
    private bool isAnalyzing;
    private string gameAnalysisText = "（等待 AI 走子後開始分析...）";
    private CancellationTokenSource? analysisCts;
    // Dispose 後防止 Task.Run 背景執行緒繼續更新 UI
    private volatile bool disposed;

    /// <summary>使用者點選 MultiPV 清單中某走法時觸發；null 表示清除選取。</summary>
    public event Action<Move?>? MultiPvMoveSelected;

    public IEnumerable<GameMode> GameModes => Enum.GetValues<GameMode>();

    public GameMode SelectedMode
    {
        get => selectedMode;
        set
        {
            if (SetProperty(ref selectedMode, value))
            {
                OnPropertyChanged(nameof(IsAiVsAiMode));
                OnPropertyChanged(nameof(IsPlayerVsAiMode));
                OnPropertyChanged(nameof(ShowDualTTStats));
            }
        }
    }

    public bool IsAiVsAiMode => selectedMode == GameMode.AiVsAi;
    public bool IsPlayerVsAiMode => selectedMode == GameMode.PlayerVsAi;
    public bool IsInSetupMode => gameService.IsInSetupMode;

    public PieceColor PlayerColor
    {
        get => playerColor;
        set
        {
            if (SetProperty(ref playerColor, value))
                gameService.PlayerColor = value;
        }
    }

    public IReadOnlyList<PlayerColorOption> PlayerColorOptions { get; } = new[]
    {
        new PlayerColorOption(PieceColor.Red, "紅方（先攻）"),
        new PlayerColorOption(PieceColor.Black, "黑方（後攻）")
    };

    public string StatusMessage
    {
        get => statusMessage;
        set => SetProperty(ref statusMessage, value);
    }

    public int SelectedTabIndex
    {
        get => selectedTabIndex;
        set => SetProperty(ref selectedTabIndex, value);
    }

    // ─── 全域設定（非 AiVsAi 模式使用）────────────────────────────────────

    public int SearchDepth
    {
        get => searchDepth;
        set
        {
            if (SetProperty(ref searchDepth, value))
            {
                gameService.SetDifficulty(value, searchThinkingTime * 1000);
            }
        }
    }

    public int SearchThinkingTime
    {
        get => searchThinkingTime;
        set
        {
            if (SetProperty(ref searchThinkingTime, value))
            {
                gameService.SetDifficulty(searchDepth, value * 1000);
            }
        }
    }

    // ─── AiVsAi 紅方設定 ────────────────────────────────────────────────

    public int RedSearchDepth
    {
        get => redSearchDepth;
        set
        {
            if (SetProperty(ref redSearchDepth, value))
            {
                gameService.SetRedAiDifficulty(value, redSearchThinkingTime * 1000);
            }
        }
    }

    public int RedSearchThinkingTime
    {
        get => redSearchThinkingTime;
        set
        {
            if (SetProperty(ref redSearchThinkingTime, value))
            {
                gameService.SetRedAiDifficulty(redSearchDepth, value * 1000);
            }
        }
    }

    // ─── AiVsAi 黑方設定 ────────────────────────────────────────────────

    public int BlackSearchDepth
    {
        get => blackSearchDepth;
        set
        {
            if (SetProperty(ref blackSearchDepth, value))
            {
                gameService.SetBlackAiDifficulty(value, blackSearchThinkingTime * 1000);
            }
        }
    }

    public int BlackSearchThinkingTime
    {
        get => blackSearchThinkingTime;
        set
        {
            if (SetProperty(ref blackSearchThinkingTime, value))
            {
                gameService.SetBlackAiDifficulty(blackSearchDepth, value * 1000);
            }
        }
    }

    // ─── TT 共用設定 ─────────────────────────────────────────────────────

    public bool UseSharedTT
    {
        get => useSharedTT;
        set
        {
            if (SetProperty(ref useSharedTT, value))
            {
                gameService.UseSharedTranspositionTable = value;
                OnPropertyChanged(nameof(ShowDualTTStats));
            }
        }
    }

    public bool CopyRedTtToBlackAtStart
    {
        get => copyRedTtToBlackAtStart;
        set
        {
            if (SetProperty(ref copyRedTtToBlackAtStart, value))
            {
                gameService.CopyRedTtToBlackAtStart = value;
            }
        }
    }

    // 獨立TT且AI對AI模式才顯示雙欄統計
    public bool ShowDualTTStats => IsAiVsAiMode && !useSharedTT;

    // ─── 智能提示 ─────────────────────────────────────────────────────────

    public bool IsSmartHintEnabled
    {
        get => isSmartHintEnabled;
        set
        {
            if (SetProperty(ref isSmartHintEnabled, value))
            {
                gameService.IsSmartHintEnabled = value;
            }
        }
    }

    public int SmartHintDepth
    {
        get => smartHintDepth;
        set
        {
            if (SetProperty(ref smartHintDepth, value))
            {
                gameService.SmartHintDepth = value;
            }
        }
    }

    // ─── MultiPV 提示 ─────────────────────────────────────────────────────

    public bool IsMultiPvHintEnabled
    {
        get => isMultiPvHintEnabled;
        set
        {
            if (SetProperty(ref isMultiPvHintEnabled, value))
                gameService.IsMultiPvHintEnabled = value;
        }
    }

    public int MultiPvCount
    {
        get => multiPvCount;
        set
        {
            if (SetProperty(ref multiPvCount, value))
                gameService.MultiPvCount = value;
        }
    }

    public IReadOnlyList<MultiPvItemViewModel> MultiPvItems
    {
        get => multiPvItems;
        private set => SetProperty(ref multiPvItems, value);
    }

    // ─── 限時模式 ─────────────────────────────────────────────────────────

    public bool IsTimedModeEnabled
    {
        get => isTimedModeEnabled;
        set
        {
            if (SetProperty(ref isTimedModeEnabled, value))
            {
                gameService.IsTimedModeEnabled = value;
                OnPropertyChanged(nameof(IsTimedModeSettingsVisible));
                OnPropertyChanged(nameof(IsTimeLimitControlVisible));
            }
        }
    }

    /// <summary>
    /// 是否顯示時間限制控制（限時模式下，由棋鐘管理時間，固定時限無意義）。
    /// </summary>
    public bool IsTimeLimitControlVisible => !isTimedModeEnabled;

    public int TimedModeMinutesPerPlayer
    {
        get => timedModeMinutesPerPlayer;
        set
        {
            if (SetProperty(ref timedModeMinutesPerPlayer, value))
            {
                gameService.TimedModeMinutesPerPlayer = value;
            }
        }
    }

    public bool IsTimedModeSettingsVisible => isTimedModeEnabled;

    public string RedTimeDisplay
    {
        get => redTimeDisplay;
        private set => SetProperty(ref redTimeDisplay, value);
    }

    public string BlackTimeDisplay
    {
        get => blackTimeDisplay;
        private set => SetProperty(ref blackTimeDisplay, value);
    }

    // ─── TT 統計（紅方 / 共用）────────────────────────────────────────────

    public string TtCapacity => $"{ttStats.Capacity:N0}";
    public string TtMemoryMb => $"{ttStats.MemoryMb:F1} MB";
    public string TtGeneration => ttStats.Generation.ToString();
    public string TtOccupied => $"{ttStats.OccupiedEntries:N0}";
    public string TtFillRate => $"{ttStats.FillRate:P1}";
    public string TtProbes => $"{ttStats.TotalProbes:N0}";
    public string TtHits => $"{ttStats.Hits:N0}";
    public string TtHitRate => $"{ttStats.HitRate:P1}";

    // ─── TT 統計（黑方，獨立TT模式）──────────────────────────────────────

    public string BlackTtCapacity => $"{blackTtStats?.Capacity ?? 0:N0}";
    public string BlackTtMemoryMb => $"{blackTtStats?.MemoryMb ?? 0:F1} MB";
    public string BlackTtGeneration => (blackTtStats?.Generation ?? 0).ToString();
    public string BlackTtOccupied => $"{blackTtStats?.OccupiedEntries ?? 0:N0}";
    public string BlackTtFillRate => $"{blackTtStats?.FillRate ?? 0:P1}";
    public string BlackTtProbes => $"{blackTtStats?.TotalProbes ?? 0:N0}";
    public string BlackTtHits => $"{blackTtStats?.Hits ?? 0:N0}";
    public string BlackTtHitRate => $"{blackTtStats?.HitRate ?? 0:P1}";

    // ─── 搜尋效能 ─────────────────────────────────────────────────────────

    public string SearchNodes => $"{gameService.LastSearchNodes:N0}";
    public string SearchNps => $"{gameService.LastSearchNps:N0} 節點/秒";

    // ─── TT 探索 ──────────────────────────────────────────────────────────

    public string TTExplorerText
    {
        get => ttExplorerText;
        private set => SetProperty(ref ttExplorerText, value);
    }

    public string HintExplanationText
    {
        get => hintExplanationText;
        private set
        {
            if (SetProperty(ref hintExplanationText, value))
            {
                OnPropertyChanged(nameof(CanExplainHint));
            }
        }
    }

    public bool CanExplainHint => !string.IsNullOrWhiteSpace(HintExplanationText) && !HintExplanationText.StartsWith("（尚未", StringComparison.Ordinal);

    // ─── 局面分析屬性 ─────────────────────────────────────────────────────

    public string GameAnalysisDisclaimer => gameAnalysisDisclaimer;

    public bool IsGameAnalysisEnabled
    {
        get => isGameAnalysisEnabled;
        set => SetProperty(ref isGameAnalysisEnabled, value);
    }

    public bool IsAnalyzing
    {
        get => isAnalyzing;
        private set => SetProperty(ref isAnalyzing, value);
    }

    public string GameAnalysisText
    {
        get => gameAnalysisText;
        private set => SetProperty(ref gameAnalysisText, value);
    }

    // ─── 指令 ─────────────────────────────────────────────────────────────

    public ICommand StartGameCommand { get; private set; } = null!;
    public ICommand UndoCommand { get; private set; } = null!;
    public ICommand HintCommand { get; private set; } = null!;
    public ICommand ExplainHintCommand { get; private set; } = null!;
    public ICommand StopThinkingCommand { get; private set; } = null!;
    public ICommand PauseThinkingCommand { get; private set; } = null!;
    public ICommand ResumeThinkingCommand { get; private set; } = null!;
    public ICommand RequestDrawCommand { get; private set; } = null!;
    public ICommand ExportTranspositionTableCommand { get; private set; } = null!;
    public ICommand ImportTranspositionTableCommand { get; private set; } = null!;
    public ICommand ExportBlackTranspositionTableCommand { get; private set; } = null!;
    public ICommand ImportBlackTranspositionTableCommand { get; private set; } = null!;
    public ICommand RefreshTTStatsCommand { get; private set; } = null!;
    public ICommand MergeTranspositionTablesCommand { get; private set; } = null!;
    public ICommand SetDifficultyPresetCommand { get; private set; } = null!;
    public ICommand EnterSetupModeCommand { get; private set; } = null!;

    /// <summary>擺棋模式 ViewModel（側邊欄顯示用）。</summary>
    public BoardSetupViewModel? BoardSetup { get; private set; }

    /// <summary>外部引擎 / 伺服器設定的 ViewModel（供 ExternalEngineView 綁定）。</summary>
    public ExternalEngineViewModel? ExternalEngine { get; }

    /// <summary>NNUE 設定的 ViewModel（供 NnueView 綁定）。</summary>
    public NnueViewModel? Nnue { get; }

    /// <summary>棋譜側邊欄 ViewModel。</summary>
    public MoveHistoryViewModel? MoveHistory { get; }

    /// <summary>殘局庫 ViewModel。</summary>
    public EndgameTablebViewModel? EndgameTableb { get; }

    // ─── 外部引擎快速開關（設定頁 Tab 1）─────────────────────────────────

    /// <summary>直接委派至 ExternalEngine，永遠與其同步。</summary>
    public bool IsRedExternalEngineEnabled
    {
        get => ExternalEngine?.UseRedExternalEngine ?? false;
        set
        {
            if (ExternalEngine != null && value != ExternalEngine.UseRedExternalEngine)
                _ = ToggleExternalEngineAsync(isRed: true, value);
        }
    }

    public bool IsBlackExternalEngineEnabled
    {
        get => ExternalEngine?.UseBlackExternalEngine ?? false;
        set
        {
            if (ExternalEngine != null && value != ExternalEngine.UseBlackExternalEngine)
                _ = ToggleExternalEngineAsync(isRed: false, value);
        }
    }

    /// <summary>紅方已設定引擎路徑（決定快速開關 CheckBox 是否可用）。</summary>
    public bool HasRedEngineConfig => ExternalEngine?.HasRedEngineConfig ?? false;

    /// <summary>黑方已設定引擎路徑（決定快速開關 CheckBox 是否可用）。</summary>
    public bool HasBlackEngineConfig => ExternalEngine?.HasBlackEngineConfig ?? false;

    public ControlPanelViewModel(IGameService gameService, GameSettings settings, IGameAnalysisService? gameAnalysisService = null, GameAnalysisSettings? analysisSettings = null, ExternalEngineViewModel? externalEngineViewModel = null, MoveHistoryViewModel? moveHistoryViewModel = null, NnueViewModel? nnueViewModel = null, EndgameTablebViewModel? endgameTablebViewModel = null)
    {
        ExternalEngine = externalEngineViewModel;
        MoveHistory = moveHistoryViewModel;
        Nnue = nnueViewModel;
        EndgameTableb = endgameTablebViewModel;
        this.gameService = gameService;
        this.gameAnalysisService = gameAnalysisService;

        InitializeSettings(settings, analysisSettings);
        InitializeCommands();
        SubscribeToEvents();
    }

    // ─── 建構子輔助方法 ────────────────────────────────────────────────────────

    private void InitializeSettings(GameSettings settings, GameAnalysisSettings? analysisSettings)
    {
        isGameAnalysisEnabled   = analysisSettings?.IsEnabled ?? false;
        gameAnalysisDisclaimer  = analysisSettings?.Disclaimer ?? "以下分析由 AI 產生，僅供參考，不代表最終結論。";

        searchDepth            = settings.SearchDepth;
        searchThinkingTime     = settings.SearchThinkingTimeSeconds;
        redSearchDepth         = settings.RedSearchDepth;
        redSearchThinkingTime  = settings.RedSearchThinkingTimeSeconds;
        blackSearchDepth       = settings.BlackSearchDepth;
        blackSearchThinkingTime = settings.BlackSearchThinkingTimeSeconds;
        useSharedTT            = settings.UseSharedTranspositionTable;
        copyRedTtToBlackAtStart = settings.CopyRedTtToBlackAtStart;
        isSmartHintEnabled     = settings.IsSmartHintEnabled;
        smartHintDepth         = settings.SmartHintDepth;
        isMultiPvHintEnabled   = settings.IsMultiPvHintEnabled;
        multiPvCount           = settings.MultiPvCount;
        isTimedModeEnabled     = settings.IsTimedModeEnabled;
        timedModeMinutesPerPlayer = settings.TimedModeMinutesPerPlayer;
        playerColor            = settings.PlayerColor;

        gameService.IsSmartHintEnabled    = isSmartHintEnabled;
        gameService.SmartHintDepth        = smartHintDepth;
        gameService.IsMultiPvHintEnabled  = isMultiPvHintEnabled;
        gameService.MultiPvCount          = multiPvCount;
        gameService.UseSharedTranspositionTable = useSharedTT;
        gameService.CopyRedTtToBlackAtStart   = copyRedTtToBlackAtStart;
        gameService.IsTimedModeEnabled         = isTimedModeEnabled;
        gameService.TimedModeMinutesPerPlayer  = timedModeMinutesPerPlayer;
        gameService.PlayerColor               = playerColor;
    }

    private void InitializeCommands()
    {
        RefreshTTStatsCommand = new RelayCommand(_ => RefreshTTStats());
        StartGameCommand = new AsyncRelayCommand(async _ =>
        {
            try { await gameService.StartGameAsync(SelectedMode); }
            catch (Exception ex) { StatusMessage = $"開始遊戲失敗：{ex.Message}"; }
        });
        UndoCommand = new RelayCommand(_ => gameService.Undo());

        EnterSetupModeCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                await gameService.EnterSetupModeAsync();
                StatusMessage = "已進入擺棋模式";
                // IsInSetupMode 通知由 gameService.SetupModeChanged → OnSetupModeChanged 負責
            }
            catch (Exception ex)
            {
                StatusMessage = $"進入擺棋模式失敗：{ex.Message}";
            }
        });

        BoardSetup = new BoardSetupViewModel(gameService);
        BoardSetup.SetupConfirmed += OnSetupConfirmed;

        RequestDrawCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "提和請求中...";
                await gameService.RequestDrawAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"提和失敗：{ex.Message}";
            }
        });

        StopThinkingCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "停止思考中...";
                await gameService.StopGameAsync();
                StatusMessage = "AI 思考已停止";
            }
            catch (Exception ex) { StatusMessage = $"停止思考失敗：{ex.Message}"; }
        });
        PauseThinkingCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "正在暫停思考...";
                await gameService.PauseThinkingAsync();
            }
            catch (Exception ex) { StatusMessage = $"暫停思考失敗：{ex.Message}"; }
        });
        ResumeThinkingCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "繼續思考中...";
                await gameService.ResumeThinkingAsync();
            }
            catch (Exception ex) { StatusMessage = $"繼續思考失敗：{ex.Message}"; }
        });

        ExportTranspositionTableCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "匯出 TT 表",
                    Filter = "Chinese Chess TT|*.cctt;*.json|Binary|*.cctt|JSON|*.json|All files|*.*",
                    FileName = $"transposition-table-{DateTime.Now:yyyyMMdd_HHmmss}.cctt"
                };

                if (dialog.ShowDialog() != true) return;

                var asJson = string.Equals(
                    Path.GetExtension(dialog.FileName),
                    ".json",
                    StringComparison.OrdinalIgnoreCase);

                StatusMessage = "匯出 TT 表中...";
                await gameService.ExportTranspositionTableAsync(dialog.FileName, asJson);
                StatusMessage = "TT 表匯出完成";
            }
            catch (Exception ex)
            {
                StatusMessage = $"TT 表匯出失敗：{ex.Message}";
            }
        });

        ImportTranspositionTableCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "匯入 TT 表",
                    Filter = "Chinese Chess TT|*.cctt;*.json|Binary|*.cctt|JSON|*.json|All files|*.*",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog() != true) return;

                var asJson = string.Equals(
                    Path.GetExtension(dialog.FileName),
                    ".json",
                    StringComparison.OrdinalIgnoreCase);

                StatusMessage = "匯入 TT 表中...";
                await gameService.ImportTranspositionTableAsync(dialog.FileName, asJson);
                StatusMessage = "TT 表匯入完成";
            }
            catch (Exception ex)
            {
                StatusMessage = $"TT 表匯入失敗：{ex.Message}";
            }
        });

        ExportBlackTranspositionTableCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "匯出黑方 TT 表",
                    Filter = "Chinese Chess TT|*.cctt;*.json|Binary|*.cctt|JSON|*.json|All files|*.*",
                    FileName = $"transposition-table-black-{DateTime.Now:yyyyMMdd_HHmmss}.cctt"
                };

                if (dialog.ShowDialog() != true) return;

                var asJson = string.Equals(
                    Path.GetExtension(dialog.FileName),
                    ".json",
                    StringComparison.OrdinalIgnoreCase);

                StatusMessage = "匯出黑方 TT 表中...";
                await gameService.ExportBlackTranspositionTableAsync(dialog.FileName, asJson);
                StatusMessage = "黑方 TT 表匯出完成";
            }
            catch (Exception ex)
            {
                StatusMessage = $"黑方 TT 表匯出失敗：{ex.Message}";
            }
        });

        ImportBlackTranspositionTableCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "匯入黑方 TT 表",
                    Filter = "Chinese Chess TT|*.cctt;*.json|Binary|*.cctt|JSON|*.json|All files|*.*",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog() != true) return;

                var asJson = string.Equals(
                    Path.GetExtension(dialog.FileName),
                    ".json",
                    StringComparison.OrdinalIgnoreCase);

                StatusMessage = "匯入黑方 TT 表中...";
                await gameService.ImportBlackTranspositionTableAsync(dialog.FileName, asJson);
                StatusMessage = "黑方 TT 表匯入完成";
            }
            catch (Exception ex)
            {
                StatusMessage = $"黑方 TT 表匯入失敗：{ex.Message}";
            }
        });

        MergeTranspositionTablesCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "合併兩方 TT 中...";
                await gameService.MergeTranspositionTablesAsync();
                StatusMessage = "TT 合併完成";
                RefreshTTStats();
            }
            catch (Exception ex)
            {
                StatusMessage = $"TT 合併失敗：{ex.Message}";
            }
        });

        SetDifficultyPresetCommand = new RelayCommand(param =>
        {
            // 根據預設等級快速填入深度與思考時間
            // 直接設定 backing fields 再呼叫一次 SetDifficulty，避免透過 property setter
            // 觸發兩次 SetDifficulty（第一次會帶入「新深度＋舊時間」的中間狀態）
            (int depth, int timeSec) = (param as string) switch
            {
                "beginner" => (2, 3),
                "casual"   => (4, 8),
                "advanced" => (6, 15),
                "expert"   => (9, 25),
                _          => (searchDepth, searchThinkingTime)
            };
            searchDepth        = depth;
            searchThinkingTime = timeSec;
            gameService.SetDifficulty(depth, timeSec * 1000);
            OnPropertyChanged(nameof(SearchDepth));
            OnPropertyChanged(nameof(SearchThinkingTime));
        });

        HintCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "提示走法中...";
                var hint = await gameService.GetHintAsync();

                if (hint.BestMove.IsNull)
                {
                    StatusMessage = "目前沒有可用提示";
                }
                else
                {
                    var turn     = gameService.CurrentBoard.Turn;
                    var notation = MoveNotation.ToNotation(hint.BestMove, gameService.CurrentBoard);
                    StatusMessage = $"提示完成：{notation} | 分數：{FormatHintScore(hint.Score)}（{(turn == PieceColor.Red ? "紅方" : "黑方")}）";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"提示失敗：{ex.Message}";
            }
        });

        ExplainHintCommand = new AsyncRelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "提示解釋中...";
                SelectedTabIndex = HintExplanationTabIndex;
                var statusPrefix = "（AI 正在解釋）";
                HintExplanationText = statusPrefix;

                var streamingText = new StringBuilder();
                var progress = new Progress<string>(text =>
                {
                    streamingText.Clear();
                    streamingText.Append(text);

                    var app = global::System.Windows.Application.Current;
                    if (app == null)
                    {
                        HintExplanationText = $"{statusPrefix}{Environment.NewLine}{streamingText}";
                        return;
                    }

                    app.Dispatcher.InvokeAsync(() =>
                    {
                        HintExplanationText = $"{statusPrefix}{Environment.NewLine}{streamingText}";
                    });
                });

                var explanation = await gameService.ExplainLatestHintAsync(progress);
                HintExplanationText = explanation;
                StatusMessage = "提示解釋完成";
            }
            catch (Exception ex)
            {
                HintExplanationText = $"解釋失敗：{ex.Message}";
                StatusMessage = "提示解釋失敗";
                SelectedTabIndex = HintExplanationTabIndex;
            }
        });

        // TT 探索計時器：每秒在背景執行緒更新，透過 Dispatcher 推送至 UI
        ttExplorerTimer = new System.Timers.Timer(100);
        ttExplorerTimer.AutoReset = true;
        ttExplorerTimer.Elapsed += (_, _) => ScheduleTTExplorerRefresh();
        ttExplorerTimer.Start();

        // 棋鐘顯示計時器：每秒更新一次，顯示雙方剩餘時間
        clockDisplayTimer = new System.Timers.Timer(500);
        clockDisplayTimer.AutoReset = true;
        clockDisplayTimer.Elapsed += (_, _) => UpdateClockDisplay();
        clockDisplayTimer.Start();

        gameService.SetDifficulty(searchDepth, searchThinkingTime * 1000);
        RefreshTTStats();
    }

    private void SubscribeToEvents()
    {
        if (ExternalEngine != null)
            ExternalEngine.PropertyChanged += OnExternalEnginePropertyChanged;

        gameService.GameMessage += OnGameMessage;
        gameService.ThinkingProgress += OnThinkingProgress;
        gameService.HintReady += OnHintReady;
        gameService.HintUpdated += OnHintUpdated;
        gameService.BoardUpdated += OnBoardUpdated;
        gameService.DrawOffered += OnDrawOffered;
        gameService.DrawOfferResolved += OnDrawOfferResolved;
        gameService.MoveCompleted += OnMoveCompleted;
        gameService.MultiPvHintReady += OnMultiPvHintReady;
        gameService.SetupModeChanged += OnSetupModeChanged;
    }

    // ─── GameService 事件處理（具名方法，供建構子訂閱及 Dispose 取消訂閱）──────

    private void OnGameMessage(string msg)
    {
        var app = global::System.Windows.Application.Current;
        if (app == null) { StatusMessage = msg; return; }
        app.Dispatcher.InvokeAsync(() => StatusMessage = msg);
    }

    private void OnThinkingProgress(string _)
    {
        var app = global::System.Windows.Application.Current;
        if (app == null) return;
        app.Dispatcher.InvokeAsync(RefreshTTStats);
    }

    private void OnHintReady(SearchResult _)
    {
        var app = global::System.Windows.Application.Current;
        if (app == null)
        {
            HintExplanationText = "（已取得提示，可按解釋）";
            return;
        }
        app.Dispatcher.InvokeAsync(() => HintExplanationText = "（已取得提示，可按解釋）");
    }

    /// <summary>
    /// 提示搜尋進行中，每個迭代深度完成時觸發，即時更新狀態列顯示目前最佳著法（非最終結果）。
    /// </summary>
    private void OnHintUpdated(SearchResult hint)
    {
        var app = global::System.Windows.Application.Current;

        if (hint.BestMove.IsNull) return;

        var notation = MoveNotation.ToNotation(hint.BestMove, gameService.CurrentBoard);
        var message = $"提示搜尋中 (非最終結果)：{notation} (深度 {hint.Depth})";

        if (app == null)
        {
            StatusMessage = message;
            return;
        }
        app.Dispatcher.InvokeAsync(() => StatusMessage = message);
    }

    private void OnBoardUpdated()
    {
        var app = global::System.Windows.Application.Current;
        void Reset()
        {
            HintExplanationText = "（尚未產生提示）";
            MultiPvItems = [];
            if (selectedMultiPvItem != null)
            {
                selectedMultiPvItem = null;
                MultiPvMoveSelected?.Invoke(null);
            }
        }

        if (app == null) Reset();
        else app.Dispatcher.InvokeAsync(Reset);
    }

    private void OnSetupModeChanged()
    {
        var app = global::System.Windows.Application.Current;
        if (app == null) OnPropertyChanged(nameof(IsInSetupMode));
        else app.Dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(IsInSetupMode)));
    }

    private void OnSetupConfirmed(GameMode mode)
    {
        StatusMessage = "局面確認！遊戲繼續中...";
        // IsInSetupMode 通知由 gameService.SetupModeChanged → OnSetupModeChanged 負責
    }

    private void SelectMultiPvItem(MultiPvItemViewModel item)
    {
        // 取消舊選取
        if (selectedMultiPvItem != null)
            selectedMultiPvItem.IsSelected = false;

        selectedMultiPvItem = item;
        item.IsSelected = true;
        MultiPvMoveSelected?.Invoke(item.Move);
    }

    private void OnMultiPvHintReady(IReadOnlyList<MoveEvaluation> evaluations)
    {
        var app = global::System.Windows.Application.Current;
        void Update()
        {
            var board = gameService.CurrentBoard;
            MultiPvItems = evaluations
                .Select((e, i) => new MultiPvItemViewModel(i + 1, e, board, SelectMultiPvItem))
                .ToList();

            // 預設選中 rank#1
            var rank1 = MultiPvItems.FirstOrDefault(x => x.IsBest) ?? MultiPvItems.FirstOrDefault();
            if (rank1 != null)
                SelectMultiPvItem(rank1);

            SelectedTabIndex = HintExplanationTabIndex;
        }

        if (app == null) Update();
        else app.Dispatcher.InvokeAsync(Update);
    }

    private void OnMoveCompleted(MoveCompletedEventArgs args)
    {
        // Triggered after an AI move completes when analysis is enabled.
        if (!isGameAnalysisEnabled || gameAnalysisService == null) return;

        // 取消上一次尚未完成的分析
        analysisCts?.Cancel();
        analysisCts?.Dispose();
        analysisCts = new CancellationTokenSource();
        var currentCts = analysisCts;

        var request = new GameAnalysisRequest
        {
            Fen             = args.Fen,
            MovedBy         = args.MovedBy,
            LastMoveNotation = args.MoveNotation,
            Score           = args.Score,
            SearchDepth     = args.Depth,
            Nodes           = args.Nodes,
            PrincipalVariation = args.PvLine,
            MoveNumber      = args.MoveNumber
        };

        var app = global::System.Windows.Application.Current;

        // 設定 loading 狀態（切換回 UI 執行緒）
        // disposed guard 防止 Dispose 後 Task.Run 背景執行緒繼續更新 UI
        void SetUiState(Action action)
        {
            if (disposed) return;
            if (app == null) action();
            else app.Dispatcher.InvokeAsync(action);
        }

        SetUiState(() =>
        {
            IsAnalyzing     = true;
            GameAnalysisText = "AI 分析中...";
        });

        // fire-and-forget（觀察例外，不阻塞 AI 搜尋執行緒）
        Task.Run(async () =>
        {
            try
            {
                var streamBuilder = new StringBuilder();
                var progress = new Progress<string>(text =>
                {
                    streamBuilder.Clear();
                    streamBuilder.Append(text);
                    SetUiState(() => GameAnalysisText = streamBuilder.ToString());
                });

                var result = await gameAnalysisService.AnalyzeAsync(request, progress, currentCts.Token);

                SetUiState(() =>
                {
                    GameAnalysisText = result;
                    IsAnalyzing      = false;
                });
            }
            catch (OperationCanceledException)
            {
                // 被新一步取消，不更新 UI
            }
            catch (Exception ex)
            {
                SetUiState(() =>
                {
                    GameAnalysisText = $"分析失敗：{ex.Message}";
                    IsAnalyzing      = false;
                });
            }
        }, currentCts.Token).ContinueWith(
            t => SetUiState(() => { GameAnalysisText = $"分析失敗：{t.Exception?.InnerException?.Message}"; IsAnalyzing = false; }),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void OnDrawOffered(DrawOfferResult offerResult)
    {
        var app = global::System.Windows.Application.Current;
        if (app == null) return;
        app.Dispatcher.InvokeAsync(() =>
        {
            var answer = MessageBox.Show(
                $"AI 提議和棋。\n{offerResult.Reason}\n\n是否接受？",
                "AI 提和",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            gameService.RespondToDrawOffer(answer == MessageBoxResult.Yes);
        });
    }

    private void OnDrawOfferResolved(DrawOfferResult result)
    {
        var app = global::System.Windows.Application.Current;
        if (app == null) return;
        string message = result.Accepted
            ? $"和棋成立！{result.Reason}"
            : $"提和遭拒。{result.Reason}";
        app.Dispatcher.InvokeAsync(() => StatusMessage = message);
    }

    private void RefreshTTStats()
    {
        ttStats      = gameService.GetTTStatistics();
        blackTtStats = gameService.GetBlackTTStatistics();

        OnPropertyChanged(nameof(TtCapacity));
        OnPropertyChanged(nameof(TtMemoryMb));
        OnPropertyChanged(nameof(TtGeneration));
        OnPropertyChanged(nameof(TtOccupied));
        OnPropertyChanged(nameof(TtFillRate));
        OnPropertyChanged(nameof(TtProbes));
        OnPropertyChanged(nameof(TtHits));
        OnPropertyChanged(nameof(TtHitRate));

        OnPropertyChanged(nameof(BlackTtCapacity));
        OnPropertyChanged(nameof(BlackTtMemoryMb));
        OnPropertyChanged(nameof(BlackTtGeneration));
        OnPropertyChanged(nameof(BlackTtOccupied));
        OnPropertyChanged(nameof(BlackTtFillRate));
        OnPropertyChanged(nameof(BlackTtProbes));
        OnPropertyChanged(nameof(BlackTtHits));
        OnPropertyChanged(nameof(BlackTtHitRate));

        OnPropertyChanged(nameof(SearchNodes));
        OnPropertyChanged(nameof(SearchNps));
        OnPropertyChanged(nameof(ShowDualTTStats));
    }

    /// <summary>
    /// 計時器觸發時呼叫：若上一次更新尚未完成則跳過（防止重疊）。
    /// 在 Task.Run 背景執行緒產生文字，再透過 Dispatcher 更新 UI。
    /// </summary>
    private void ScheduleTTExplorerRefresh()
    {
        if (SelectedTabIndex != TTExplorerTabIndex)
            return;

        // CAS：0→1 成功才進入，否則跳過此次
        if (Interlocked.CompareExchange(ref ttExplorerBusy, 1, 0) != 0) return;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string text = BuildTTExplorerText();
                var app = global::System.Windows.Application.Current;
                app?.Dispatcher.InvokeAsync(() => TTExplorerText = text);
            }
            finally
            {
                Interlocked.Exchange(ref ttExplorerBusy, 0);
            }
        });
    }

    private string BuildTTExplorerText()
    {
        var sb = new StringBuilder();

        // ── 條目分布 ────────────────────────────────────────
        sb.AppendLine("══ TT 條目分布 ══════════════════════════════");
        try
        {
            var depthCounts = new long[256];
            var flagCounts = new long[4];
            long total = 0;

            foreach (var entry in gameService.EnumerateTTEntries())
            {
                total++;
                depthCounts[entry.Depth]++;

                int flagIdx = (int)entry.Flag;
                if (flagIdx >= 0 && flagIdx < flagCounts.Length)
                {
                    flagCounts[flagIdx]++;
                }
            }

            sb.AppendLine($"有效條目：{total:N0}");
            sb.AppendLine();

            if (total > 0)
            {
                // 深度分布（附簡易長條圖）
                sb.AppendLine("深度分布：");
                int maxCount = 0;
                for (int i = 0; i < depthCounts.Length; i++)
                {
                    if (depthCounts[i] > maxCount) maxCount = (int)depthCounts[i];
                }

                const int BarWidth = 20;
                for (int depth = 0; depth < depthCounts.Length; depth++)
                {
                    long count = depthCounts[depth];
                    if (count == 0) continue;

                    int bars = maxCount > 0 ? (int)Math.Round((double)count / maxCount * BarWidth) : 0;
                    string bar = new string('█', bars).PadRight(BarWidth);
                    sb.AppendLine($"  深度 {depth,2}：{count,7:N0} {bar}");
                }

                sb.AppendLine();
                sb.AppendLine("旗標分布：");
                foreach (TTFlag flag in Enum.GetValues<TTFlag>())
                {
                    int flagIdx = (int)flag;
                    if (flagIdx < 0 || flagIdx >= flagCounts.Length) continue;
                    long count = flagCounts[flagIdx];
                    if (count == 0) continue;
                    sb.AppendLine($"  {flag,-12}：{count,7:N0}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"（枚舉失敗：{ex.Message}）");
        }

        sb.AppendLine();

        // ── 思路樹 ───────────────────────────────────────────
        sb.AppendLine("══ 思路樹 ═══════════════════════════════════");
        try
        {
            // 取快照以確保棋盤在整個遞迴中不被修改
            var boardSnapshot = gameService.CurrentBoard.Clone();
            var root = gameService.ExploreTTTree(TTExploreMaxDepth);
            if (root == null)
            {
                sb.AppendLine("（當前局面不在 TT 中，尚未搜尋過此局面）");
            }
            else
            {
                AppendTreeNode(sb, root, "", isRoot: true, parentBoard: boardSnapshot);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"（探索失敗：{ex.Message}）");
        }

        return sb.ToString();
    }

    private static void AppendTreeNode(
        System.Text.StringBuilder sb,
        TTTreeNode node,
        string indent,
        bool isRoot,
        IBoard parentBoard)
    {
        var e = node.Entry;
        string scoreStr = e.Score > 0 ? $"+{e.Score}" : e.Score.ToString();
        string flagChar = e.Flag switch
        {
            TTFlag.Exact      => "=",
            TTFlag.LowerBound => "≥",
            TTFlag.UpperBound => "≤",
            _                 => "?"
        };

        // 根節點顯示「當前局面」；子節點用 parentBoard（走法執行前的棋盤）轉換標準記譜
        string moveStr;
        IBoard boardAtNode;
        if (isRoot)
        {
            moveStr = "（當前局面）";
            boardAtNode = parentBoard;
        }
        else
        {
            moveStr = MoveNotation.ToNotation(node.MoveToHere, parentBoard);
            boardAtNode = parentBoard.Clone();
            try { boardAtNode.MakeMove(node.MoveToHere); }
            catch { /* 過期或碰撞條目，無法套用走法；子節點仍可顯示但不再遞迴 */ }
        }

        sb.AppendLine($"{indent}{moveStr}  [{flagChar} {scoreStr}, 深度:{e.Depth}]");

        foreach (var child in node.Children)
            AppendTreeNode(sb, child, indent + "  ", isRoot: false, parentBoard: boardAtNode);
    }

    /// <summary>
    /// ExternalEngine 屬性變更時，通知 UI 重新讀取本 ViewModel 的計算屬性。
    /// </summary>
    private void OnExternalEnginePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        string? localName = e.PropertyName switch
        {
            nameof(ExternalEngineViewModel.UseRedExternalEngine)   => nameof(IsRedExternalEngineEnabled),
            nameof(ExternalEngineViewModel.UseBlackExternalEngine) => nameof(IsBlackExternalEngineEnabled),
            nameof(ExternalEngineViewModel.HasRedEngineConfig)     => nameof(HasRedEngineConfig),
            nameof(ExternalEngineViewModel.HasBlackEngineConfig)   => nameof(HasBlackEngineConfig),
            _ => null
        };

        if (localName == null) return;

        var app = global::System.Windows.Application.Current;
        if (app == null) OnPropertyChanged(localName);
        else app.Dispatcher.InvokeAsync(() => OnPropertyChanged(localName));
    }

    /// <summary>
    /// 設定頁快速開關外部引擎：失敗時觸發計算屬性重讀（CheckBox 自動恢復）並提示。
    /// </summary>
    private async Task ToggleExternalEngineAsync(bool isRed, bool enable)
    {
        if (ExternalEngine == null) return;

        bool success = await ExternalEngine.ToggleEngineAsync(isRed, enable);

        if (!success)
        {
            // ExternalEngine 未變動，通知 UI 重讀計算屬性讓 CheckBox 恢復原狀
            OnPropertyChanged(isRed ? nameof(IsRedExternalEngineEnabled) : nameof(IsBlackExternalEngineEnabled));
            StatusMessage = "請先至「外部引擎」頁設定引擎路徑";
        }
    }

    private static string FormatHintScore(int score)
    {
        return score switch
        {
            > 0 => $"+{score}",
            < 0 => score.ToString(),
            _ => "0"
        };
    }

    private void UpdateClockDisplay()
    {
        var clock = gameService.Clock;
        string red, black;

        if (clock == null)
        {
            red   = "--:--";
            black = "--:--";
        }
        else
        {
            red   = FormatTime(clock.RedRemaining);
            black = FormatTime(clock.BlackRemaining);
        }

        var app = global::System.Windows.Application.Current;
        if (app == null)
        {
            RedTimeDisplay   = red;
            BlackTimeDisplay = black;
        }
        else
        {
            app.Dispatcher.InvokeAsync(() =>
            {
                RedTimeDisplay   = red;
                BlackTimeDisplay = black;
            });
        }
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "00:00";
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }

    public void Dispose()
    {
        disposed = true; // 先設定 guard，防止背景執行緒繼續更新 UI
        if (ExternalEngine != null)
            ExternalEngine.PropertyChanged -= OnExternalEnginePropertyChanged;

        // 取消訂閱所有 GameService 事件，防止 ViewModel 被 Service 持有引用而無法被 GC
        gameService.GameMessage -= OnGameMessage;
        gameService.ThinkingProgress -= OnThinkingProgress;
        gameService.HintReady -= OnHintReady;
        gameService.HintUpdated -= OnHintUpdated;
        gameService.BoardUpdated -= OnBoardUpdated;
        gameService.DrawOffered -= OnDrawOffered;
        gameService.DrawOfferResolved -= OnDrawOfferResolved;
        gameService.MoveCompleted -= OnMoveCompleted;
        gameService.MultiPvHintReady -= OnMultiPvHintReady;
        gameService.SetupModeChanged -= OnSetupModeChanged;
        if (BoardSetup != null)
            BoardSetup.SetupConfirmed -= OnSetupConfirmed;

        analysisCts?.Cancel();
        analysisCts?.Dispose();
        analysisCts = null;

        ttExplorerTimer?.Stop();
        ttExplorerTimer?.Dispose();
        clockDisplayTimer?.Stop();
        clockDisplayTimer?.Dispose();
        MoveHistory?.Dispose();
    }
}

/// <summary>玩家持色選項（供 ComboBox 顯示用）。</summary>
public sealed record PlayerColorOption(PieceColor Color, string DisplayName);

/// <summary>MultiPV 提示結果清單項目。</summary>
public sealed class MultiPvItemViewModel : ObservableObject
{
    private bool isSelected;

    public int Rank { get; }
    public Move Move { get; }
    public string Notation { get; }
    public string ScoreText { get; }
    public int Score { get; }
    public string PvLine { get; }
    public bool IsBest { get; }

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public ICommand SelectCommand { get; }

    public MultiPvItemViewModel(int rank, MoveEvaluation eval, IBoard board, Action<MultiPvItemViewModel> onSelect)
    {
        Rank = rank;
        Move = eval.Move;
        Notation = MoveNotation.ToNotation(eval.Move, board);
        Score = eval.Score;
        ScoreText = eval.Score > 0 ? $"+{eval.Score}" : eval.Score.ToString();
        PvLine = eval.PvLine;
        IsBest = eval.IsBest;
        SelectCommand = new RelayCommand(_ => onSelect(this));
    }
}
