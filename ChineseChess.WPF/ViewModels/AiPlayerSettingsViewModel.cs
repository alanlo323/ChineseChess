using ChineseChess.Application.Configuration;
using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Infrastructure.AI.Protocol;
using ChineseChess.Domain.Enums;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// 單一 AI 玩家的完整設定 ViewModel（以 PieceColor 參數化）。
/// 整合原本散落在 ControlPanelViewModel、ExternalEngineViewModel、NnueViewModel 的 per-player 設定。
/// </summary>
public sealed class AiPlayerSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IGameService gameService;
    private readonly IEngineProvider engineProvider;
    private readonly IAiEngineFactory? engineFactory;

    // ─── 引擎類型 ─────────────────────────────────────────────────────────
    private AiEngineType engineType = AiEngineType.Internal;

    // ─── 內部引擎設定 ──────────────────────────────────────────────────────
    private int searchDepth = 6;
    private InternalEvaluatorType evaluatorType = InternalEvaluatorType.Handcrafted;
    private string nnueModelPath = string.Empty;

    // ─── 外部引擎設定 ──────────────────────────────────────────────────────
    private string enginePath = string.Empty;
    private EngineProtocol protocol = EngineProtocol.Ucci;
    private string engineStatus = "未啟用";
    private IExternalEngineAdapter? adapter;

    // ─── Pikafish 設定 ─────────────────────────────────────────────────────
    private int multiPv = 1;
    private int skillLevel = 20;
    private bool uciLimitStrength;
    private int uciElo = 2850;
    private bool sixtyMoveRule = true;
    private int rule60MaxPly = 120;
    private int mateThreatDepth;
    private PikafishScoreType scoreType = PikafishScoreType.Elo;
    private bool luOutput = true;
    private PikafishDrawRule drawRule = PikafishDrawRule.None;
    private string evalFile = string.Empty;

    // ─── 通用設定 ──────────────────────────────────────────────────────────
    private int searchTimeSeconds = 3;
    private bool isTimedMode;
    private string softTimeLimitDisplay = "--";
    private string hardTimeLimitDisplay = "--";

    // ─── 建構子 ───────────────────────────────────────────────────────────

    public AiPlayerSettingsViewModel(
        PieceColor color,
        IGameService gameService,
        IEngineProvider engineProvider,
        IAiEngineFactory? engineFactory = null)
    {
        Color = color;
        this.gameService = gameService ?? throw new ArgumentNullException(nameof(gameService));
        this.engineProvider = engineProvider ?? throw new ArgumentNullException(nameof(engineProvider));
        this.engineFactory = engineFactory;

        BrowseEngineCommand = new RelayCommand(_ => BrowseEngine());
        ApplyEngineCommand = new AsyncRelayCommand(async _ => await ApplyEngineAsync());
        DisconnectEngineCommand = new RelayCommand(_ => DisconnectEngine());
        BrowseNnueModelCommand = new RelayCommand(_ => BrowseNnueModel());
        ApplyPikafishCommand = new AsyncRelayCommand(async _ =>
        {
            await ApplyPikafishSettingsAsync();
            SettingsChanged?.Invoke();
        });
        ClearHashCommand = new AsyncRelayCommand(async _ => await ClearHashAsync());
        BrowseEvalFileCommand = new RelayCommand(_ => BrowseEvalFile());
    }

    // ─── 唯讀識別 ─────────────────────────────────────────────────────────

    /// <summary>此設定面板對應的棋方。</summary>
    public PieceColor Color { get; }

    /// <summary>顯示名稱（紅方 AI / 黑方 AI）。</summary>
    public string DisplayName => Color == PieceColor.Red ? "紅方 AI" : "黑方 AI";

    /// <summary>設定變更時觸發（供父 ViewModel 執行持久化）。</summary>
    public event Action? SettingsChanged;

    // ─── 引擎類型選擇 ─────────────────────────────────────────────────────

    public AiEngineType EngineType
    {
        get => engineType;
        set
        {
            if (SetProperty(ref engineType, value))
            {
                OnPropertyChanged(nameof(IsInternalEngine));
                OnPropertyChanged(nameof(IsExternalEngine));
                SyncToService();
                SettingsChanged?.Invoke();
            }
        }
    }

    public bool IsInternalEngine => engineType == AiEngineType.Internal;
    public bool IsExternalEngine => engineType == AiEngineType.External;

    public IEnumerable<AiEngineType> EngineTypes => Enum.GetValues<AiEngineType>();

    // ─── 內部引擎：搜索深度 ───────────────────────────────────────────────

    public int SearchDepth
    {
        get => searchDepth;
        set
        {
            if (SetProperty(ref searchDepth, Math.Clamp(value, 1, 10)))
            {
                SyncDifficultyToService();
                SettingsChanged?.Invoke();
            }
        }
    }

    // ─── 內部引擎：評估器類型 ─────────────────────────────────────────────

    public InternalEvaluatorType EvaluatorType
    {
        get => evaluatorType;
        set
        {
            if (SetProperty(ref evaluatorType, value))
            {
                OnPropertyChanged(nameof(IsNnueEvaluator));
                SettingsChanged?.Invoke();
            }
        }
    }

    public bool IsNnueEvaluator => evaluatorType == InternalEvaluatorType.Nnue;

    public IEnumerable<InternalEvaluatorType> EvaluatorTypes => Enum.GetValues<InternalEvaluatorType>();

    // ─── 內部引擎：NNUE 模型路徑 ──────────────────────────────────────────

    public string NnueModelPath
    {
        get => nnueModelPath;
        set
        {
            if (SetProperty(ref nnueModelPath, value))
            {
                OnPropertyChanged(nameof(NnueModelFileExists));
                SettingsChanged?.Invoke();
            }
        }
    }

    public bool NnueModelFileExists => !string.IsNullOrEmpty(nnueModelPath) && File.Exists(nnueModelPath);

    // ─── 外部引擎：路徑與協議 ─────────────────────────────────────────────

    public string EnginePath
    {
        get => enginePath;
        set
        {
            if (SetProperty(ref enginePath, value))
            {
                OnPropertyChanged(nameof(HasEngineConfig));
                SettingsChanged?.Invoke();
            }
        }
    }

    public EngineProtocol Protocol
    {
        get => protocol;
        set
        {
            if (SetProperty(ref protocol, value))
                SettingsChanged?.Invoke();
        }
    }

    public string EngineStatus
    {
        get => engineStatus;
        set => SetProperty(ref engineStatus, value);
    }

    public bool HasEngineConfig => !string.IsNullOrWhiteSpace(enginePath);
    public bool IsPikafish => adapter?.IsPikafish ?? false;

    public IEnumerable<EngineProtocol> EngineProtocols => Enum.GetValues<EngineProtocol>();

    // ─── Pikafish 設定 ─────────────────────────────────────────────────────

    public int MultiPv
    {
        get => multiPv;
        set => SetProperty(ref multiPv, Math.Clamp(value, 1, 500));
    }

    public int SkillLevel
    {
        get => skillLevel;
        set => SetProperty(ref skillLevel, Math.Clamp(value, 0, 20));
    }

    public bool UciLimitStrength
    {
        get => uciLimitStrength;
        set
        {
            if (SetProperty(ref uciLimitStrength, value))
                OnPropertyChanged(nameof(UciEloEnabled));
        }
    }

    public int UciElo
    {
        get => uciElo;
        set => SetProperty(ref uciElo, Math.Clamp(value, 1280, 3133));
    }

    public bool SixtyMoveRule
    {
        get => sixtyMoveRule;
        set
        {
            if (SetProperty(ref sixtyMoveRule, value))
                OnPropertyChanged(nameof(Rule60MaxPlyEnabled));
        }
    }

    public int Rule60MaxPly
    {
        get => rule60MaxPly;
        set => SetProperty(ref rule60MaxPly, Math.Clamp(value, 1, 150));
    }

    public int MateThreatDepth
    {
        get => mateThreatDepth;
        set => SetProperty(ref mateThreatDepth, Math.Clamp(value, 0, 10));
    }

    public PikafishScoreType ScoreType
    {
        get => scoreType;
        set => SetProperty(ref scoreType, value);
    }

    public bool LuOutput
    {
        get => luOutput;
        set => SetProperty(ref luOutput, value);
    }

    public PikafishDrawRule DrawRule
    {
        get => drawRule;
        set => SetProperty(ref drawRule, value);
    }

    public string EvalFile
    {
        get => evalFile;
        set => SetProperty(ref evalFile, value);
    }

    // ─── Pikafish 計算屬性 ────────────────────────────────────────────────

    public bool UciEloEnabled => IsPikafish && uciLimitStrength;
    public bool Rule60MaxPlyEnabled => IsPikafish && sixtyMoveRule;

    public IEnumerable<PikafishScoreType> ScoreTypes => Enum.GetValues<PikafishScoreType>();
    public IEnumerable<PikafishDrawRule> DrawRules => Enum.GetValues<PikafishDrawRule>();

    // ─── 通用設定：搜索時間 ───────────────────────────────────────────────

    public int SearchTimeSeconds
    {
        get => searchTimeSeconds;
        set
        {
            if (SetProperty(ref searchTimeSeconds, Math.Clamp(value, 1, 120)))
            {
                SyncDifficultyToService();
                SettingsChanged?.Invoke();
            }
        }
    }

    /// <summary>是否為限時模式（由 ControlPanelViewModel 驅動）。</summary>
    public bool IsTimedMode
    {
        get => isTimedMode;
        private set
        {
            if (SetProperty(ref isTimedMode, value))
            {
                OnPropertyChanged(nameof(IsSearchTimeEditable));
            }
        }
    }

    /// <summary>非限時模式時可編輯搜索時間。</summary>
    public bool IsSearchTimeEditable => !isTimedMode;

    /// <summary>限時模式下系統計算的軟時限顯示。</summary>
    public string SoftTimeLimitDisplay
    {
        get => softTimeLimitDisplay;
        private set => SetProperty(ref softTimeLimitDisplay, value);
    }

    /// <summary>限時模式下系統計算的硬時限顯示。</summary>
    public string HardTimeLimitDisplay
    {
        get => hardTimeLimitDisplay;
        private set => SetProperty(ref hardTimeLimitDisplay, value);
    }

    // ─── 命令 ─────────────────────────────────────────────────────────────

    public ICommand BrowseEngineCommand { get; }
    public ICommand ApplyEngineCommand { get; }
    public ICommand DisconnectEngineCommand { get; }
    public ICommand BrowseNnueModelCommand { get; }
    public ICommand ApplyPikafishCommand { get; }
    public ICommand ClearHashCommand { get; }
    public ICommand BrowseEvalFileCommand { get; }

    // ─── 公開 API ─────────────────────────────────────────────────────────

    /// <summary>從 ControlPanelViewModel 通知限時模式切換。</summary>
    public void UpdateTimedMode(bool enabled)
    {
        IsTimedMode = enabled;
        if (!enabled)
        {
            SoftTimeLimitDisplay = "--";
            HardTimeLimitDisplay = "--";
        }
    }

    /// <summary>更新限時模式下的時限顯示（由 GameService 的棋鐘計算結果驅動）。</summary>
    public void UpdateTimeLimitDisplay(int softMs, int hardMs)
    {
        SoftTimeLimitDisplay = $"{softMs / 1000.0:F1}s";
        HardTimeLimitDisplay = $"{hardMs / 1000.0:F1}s";
    }

    /// <summary>套用難度預設（新手、業餘、進階、專家）。</summary>
    public void ApplyPreset(int depth, int timeSec)
    {
        // 直接設定 backing fields 避免多次觸發 SyncDifficultyToService
        SetProperty(ref searchDepth, Math.Clamp(depth, 1, 10), nameof(SearchDepth));
        SetProperty(ref searchTimeSeconds, Math.Clamp(timeSec, 1, 120), nameof(SearchTimeSeconds));
        SyncDifficultyToService();
        SettingsChanged?.Invoke();
    }

    /// <summary>將所有引擎設定同步至 GameService 和 EngineProvider。</summary>
    public void SyncToService()
    {
        SyncDifficultyToService();

        if (engineType == AiEngineType.External && adapter != null)
        {
            // 外部引擎模式
            if (Color == PieceColor.Red)
                engineProvider.SetRedExternalEngine(adapter);
            else
                engineProvider.SetBlackExternalEngine(adapter);
        }
        else
        {
            // 內部引擎模式：清除外部引擎
            if (Color == PieceColor.Red)
                engineProvider.SetRedExternalEngine(null);
            else
                engineProvider.SetBlackExternalEngine(null);
        }
    }

    /// <summary>取得當前外部引擎的 adapter（供 ControlPanelViewModel 做 TT 探索用）。</summary>
    public IExternalEngineAdapter? GetAdapter() => adapter;

    // ─── 持久化支援 ───────────────────────────────────────────────────────

    /// <summary>從持久化設定還原狀態（必須在 UI 執行緒呼叫）。</summary>
    public void RestoreFromSettings(ExternalEngineSettings saved)
    {
        bool isRed = Color == PieceColor.Red;

        // 還原引擎類型、搜索深度、時間、評估器、NNUE 路徑
        engineType = isRed ? saved.RedAiEngineType : saved.BlackAiEngineType;
        searchDepth = isRed ? saved.RedSearchDepth : saved.BlackSearchDepth;
        searchTimeSeconds = isRed ? saved.RedSearchTimeSeconds : saved.BlackSearchTimeSeconds;
        evaluatorType = isRed ? saved.RedEvaluatorType : saved.BlackEvaluatorType;
        nnueModelPath = isRed ? saved.RedNnueModelPath : saved.BlackNnueModelPath;

        // 還原外部引擎設定
        enginePath = isRed ? saved.RedEnginePath : saved.BlackEnginePath;
        protocol = isRed ? saved.RedProtocol : saved.BlackProtocol;

        // 還原 Pikafish 設定
        var pf = isRed ? saved.RedPikafish : saved.BlackPikafish;
        RestorePikafishSettings(pf);

        NotifyAllPropertiesChanged();
    }

    /// <summary>將目前完整狀態快照至設定物件（供持久化使用）。</summary>
    public void CaptureToSettings(ExternalEngineSettings target)
    {
        bool isRed = Color == PieceColor.Red;
        bool useExternal = engineType == AiEngineType.External;

        if (isRed)
        {
            target.RedAiEngineType = engineType;
            target.RedSearchDepth = searchDepth;
            target.RedSearchTimeSeconds = searchTimeSeconds;
            target.RedEvaluatorType = evaluatorType;
            target.RedNnueModelPath = nnueModelPath;
            target.UseRedExternalEngine = useExternal;
            target.RedEnginePath = enginePath;
            target.RedProtocol = protocol;
            target.RedPikafish = CapturePikafishSettings();
        }
        else
        {
            target.BlackAiEngineType = engineType;
            target.BlackSearchDepth = searchDepth;
            target.BlackSearchTimeSeconds = searchTimeSeconds;
            target.BlackEvaluatorType = evaluatorType;
            target.BlackNnueModelPath = nnueModelPath;
            target.UseBlackExternalEngine = useExternal;
            target.BlackEnginePath = enginePath;
            target.BlackProtocol = protocol;
            target.BlackPikafish = CapturePikafishSettings();
        }
    }

    // ─── 私有命令實作 ─────────────────────────────────────────────────────

    private void SyncDifficultyToService()
    {
        var timeMs = searchTimeSeconds * 1000;
        if (Color == PieceColor.Red)
            gameService.SetRedAiDifficulty(searchDepth, timeMs);
        else
            gameService.SetBlackAiDifficulty(searchDepth, timeMs);
    }

    private void BrowseEngine()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"選擇{DisplayName}引擎",
            Filter = "可執行檔|*.exe|所有檔案|*.*"
        };
        if (dialog.ShowDialog() == true)
            EnginePath = dialog.FileName;
    }

    private async Task ApplyEngineAsync()
    {
        if (string.IsNullOrWhiteSpace(enginePath))
        {
            EngineStatus = "請先選擇引擎執行檔";
            return;
        }

        EngineStatus = "初始化中...";

        var newAdapter = new ExternalEngineAdapter(enginePath, protocol);
        try
        {
            using var cts = new CancellationTokenSource(10_000);
            await newAdapter.InitializeAsync(cts.Token);

            // 設定成功：替換 adapter（先 Dispose 舊的，避免洩漏子進程）
            var oldAdapter = adapter;
            adapter = newAdapter;
            if (oldAdapter != null && !ReferenceEquals(oldAdapter, newAdapter))
                (oldAdapter as IDisposable)?.Dispose();
            OnPropertyChanged(nameof(IsPikafish));
            OnPropertyChanged(nameof(UciEloEnabled));
            OnPropertyChanged(nameof(Rule60MaxPlyEnabled));

            EngineStatus = newAdapter.IsPikafish
                ? $"已載入（{newAdapter.EngineName}）"
                : $"已載入（{protocol}）";

            // 自動切換至外部引擎模式（直接設 backing field 避免 setter 重複觸發）
            engineType = AiEngineType.External;
            OnPropertyChanged(nameof(EngineType));
            OnPropertyChanged(nameof(IsInternalEngine));
            OnPropertyChanged(nameof(IsExternalEngine));

            if (newAdapter.IsPikafish)
                await ApplyPikafishSettingsAsync();

            SyncToService();
            SettingsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            newAdapter.Dispose();
            EngineStatus = $"初始化失敗：{ex.Message}";
        }
    }

    private void DisconnectEngine()
    {
        if (Color == PieceColor.Red)
            engineProvider.SetRedExternalEngine(null);
        else
            engineProvider.SetBlackExternalEngine(null);

        adapter = null;
        EngineStatus = "已中斷連線";
        OnPropertyChanged(nameof(IsPikafish));
        OnPropertyChanged(nameof(UciEloEnabled));
        OnPropertyChanged(nameof(Rule60MaxPlyEnabled));

        // 自動切換回內部引擎（直接設 backing field 避免 setter 重複觸發）
        engineType = AiEngineType.Internal;
        OnPropertyChanged(nameof(EngineType));
        OnPropertyChanged(nameof(IsInternalEngine));
        OnPropertyChanged(nameof(IsExternalEngine));
        SyncToService();
        SettingsChanged?.Invoke();
    }

    private async Task ApplyPikafishSettingsAsync()
    {
        if (adapter == null) return;
        await SendPikafishOptionsToAdapterAsync(adapter, CapturePikafishSettings());
    }

    private static async Task SendPikafishOptionsToAdapterAsync(IExternalEngineAdapter adapter, PikafishSettings s)
    {
        await adapter.SendOptionAsync("MultiPV", s.MultiPv.ToString());
        await adapter.SendOptionAsync("Skill Level", s.SkillLevel.ToString());
        await adapter.SendOptionAsync("UCI_LimitStrength", s.UciLimitStrength ? "true" : "false");
        await adapter.SendOptionAsync("UCI_Elo", s.UciElo.ToString());
        await adapter.SendOptionAsync("Sixty Move Rule", s.SixtyMoveRule ? "true" : "false");
        await adapter.SendOptionAsync("Rule60MaxPly", s.Rule60MaxPly.ToString());
        await adapter.SendOptionAsync("MateThreatDepth", s.MateThreatDepth.ToString());
        await adapter.SendOptionAsync("ScoreType", s.ScoreType.ToString());
        await adapter.SendOptionAsync("LU_Output", s.LuOutput ? "true" : "false");
        await adapter.SendOptionAsync("DrawRule", s.DrawRule.ToString());
        if (!string.IsNullOrWhiteSpace(s.EvalFile))
            await adapter.SendOptionAsync("EvalFile", s.EvalFile);
    }

    private async Task ClearHashAsync()
    {
        if (adapter == null) return;
        await adapter.SendButtonOptionAsync("Clear Hash");
    }

    private void BrowseNnueModel()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"選取{DisplayName} NNUE 模型檔",
            Filter = "NNUE 模型 (*.nnue)|*.nnue|所有檔案|*.*",
        };
        if (dialog.ShowDialog() == true)
            NnueModelPath = dialog.FileName;
    }

    private void BrowseEvalFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"選擇{DisplayName} EvalFile",
            Filter = "NNUE 評估檔|*.nnue|所有檔案|*.*"
        };
        if (dialog.ShowDialog() == true)
            EvalFile = dialog.FileName;
    }

    private PikafishSettings CapturePikafishSettings() => new()
    {
        MultiPv = multiPv,
        SkillLevel = skillLevel,
        UciLimitStrength = uciLimitStrength,
        UciElo = uciElo,
        SixtyMoveRule = sixtyMoveRule,
        Rule60MaxPly = rule60MaxPly,
        MateThreatDepth = mateThreatDepth,
        ScoreType = scoreType,
        LuOutput = luOutput,
        DrawRule = drawRule,
        EvalFile = evalFile
    };

    private void RestorePikafishSettings(PikafishSettings pf)
    {
        multiPv = pf.MultiPv;
        skillLevel = pf.SkillLevel;
        uciLimitStrength = pf.UciLimitStrength;
        uciElo = pf.UciElo;
        sixtyMoveRule = pf.SixtyMoveRule;
        rule60MaxPly = pf.Rule60MaxPly;
        mateThreatDepth = pf.MateThreatDepth;
        scoreType = pf.ScoreType;
        luOutput = pf.LuOutput;
        drawRule = pf.DrawRule;
        evalFile = pf.EvalFile;
    }

    /// <summary>通知所有屬性已變更（WPF 慣例：傳入空字串表示全部重新綁定）。</summary>
    private void NotifyAllPropertiesChanged() => OnPropertyChanged(string.Empty);

    /// <summary>啟動時自動連接已設定的外部引擎。</summary>
    public async Task AutoConnectIfNeededAsync()
    {
        if (engineType == AiEngineType.External && !string.IsNullOrWhiteSpace(enginePath))
        {
            try
            {
                await ApplyEngineAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"{DisplayName}自動連接引擎失敗：{ex.Message}");
            }
        }
    }

    // ─── 每方獨立 NNUE 引擎支援 ──────────────────────────────────────────

    /// <summary>
    /// 套用目前的評估器設定至 EngineProvider。
    /// 需在兩方都設定完畢後由父 ViewModel 統一呼叫一次 EngineProvider.ApplyPerPlayerNnueAsync。
    /// </summary>
    public NnueEngineConfig? BuildNnueConfig()
    {
        if (engineType != AiEngineType.Internal)
            return null;
        if (evaluatorType != InternalEvaluatorType.Nnue)
            return null;
        if (string.IsNullOrEmpty(nnueModelPath) || !File.Exists(nnueModelPath))
            return null;

        return new NnueEngineConfig
        {
            ModelFilePath = nnueModelPath,
            EvaluationMode = NnueEvaluationMode.Composite,
        };
    }

    // ─── Dispose ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        (adapter as IDisposable)?.Dispose();
        adapter = null;
    }
}
