using ChineseChess.Application.Configuration;
using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Enums;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly ILoadedEngineRegistry registry;
    private readonly IAiEngineFactory? engineFactory;

    // ─── 引擎類型 ─────────────────────────────────────────────────────────
    private AiEngineType engineType = AiEngineType.Internal;

    // ─── 內部引擎設定 ──────────────────────────────────────────────────────
    private int searchDepth = 6;
    private InternalEvaluatorType evaluatorType = InternalEvaluatorType.Handcrafted;
    private string nnueModelPath = string.Empty;

    // ─── 外部引擎設定（從 Registry 選取）─────────────────────────────────
    private string? selectedEngineId;
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
        ILoadedEngineRegistry loadedEngineRegistry,
        IAiEngineFactory? engineFactory = null)
    {
        Color = color;
        this.gameService = gameService ?? throw new ArgumentNullException(nameof(gameService));
        this.engineProvider = engineProvider ?? throw new ArgumentNullException(nameof(engineProvider));
        this.registry = loadedEngineRegistry ?? throw new ArgumentNullException(nameof(loadedEngineRegistry));
        this.engineFactory = engineFactory;

        ApplyEngineCommand    = new AsyncRelayCommand(async _ => await ApplyEngineAsync());
        DisconnectEngineCommand = new RelayCommand(_ => DisconnectEngine());
        BrowseNnueModelCommand = new RelayCommand(_ => BrowseNnueModel());
        ApplyPikafishCommand = new AsyncRelayCommand(async _ =>
        {
            await ApplyPikafishSettingsAsync();
            SettingsChanged?.Invoke();
        });
        ClearHashCommand = new AsyncRelayCommand(async _ => await ClearHashAsync());
        BrowseEvalFileCommand = new RelayCommand(_ => BrowseEvalFile());

        registry.EnginesChanged += OnRegistryEnginesChanged;
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

    // ─── 外部引擎：從 Registry 選取 ──────────────────────────────────────

    /// <summary>可選的已載入引擎列表（由 Registry 提供）。</summary>
    public IReadOnlyList<LoadedEngineInfo> AvailableEngines => registry.Engines;

    /// <summary>是否沒有任何可選引擎（供「請至 NNUE Tab 載入引擎」提示用）。</summary>
    public bool HasNoEngines => registry.Engines.Count == 0;

    /// <summary>目前選取的引擎 ID（綁定 ComboBox SelectedValue）。</summary>
    public string? SelectedEngineId
    {
        get => selectedEngineId;
        set
        {
            if (SetProperty(ref selectedEngineId, value))
            {
                OnPropertyChanged(nameof(IsPikafish));
                OnPropertyChanged(nameof(UciEloEnabled));
                OnPropertyChanged(nameof(Rule60MaxPlyEnabled));
                OnPropertyChanged(nameof(HasEngineConfig));
                SettingsChanged?.Invoke();
            }
        }
    }

    public string EngineStatus
    {
        get => engineStatus;
        set => SetProperty(ref engineStatus, value);
    }

    /// <summary>是否已選取引擎（即可嘗試連接）。</summary>
    public bool HasEngineConfig => !string.IsNullOrEmpty(selectedEngineId);

    /// <summary>目前選取的引擎是否為 Pikafish（影響 Pikafish 設定面板顯示）。</summary>
    public bool IsPikafish => registry
        .GetEngineInfo(selectedEngineId ?? string.Empty)
        ?.EngineName.Contains("Pikafish", StringComparison.OrdinalIgnoreCase)
        ?? false;

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
                OnPropertyChanged(nameof(IsSearchTimeEditable));
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
            if (Color == PieceColor.Red)
                engineProvider.SetRedExternalEngine(adapter);
            else
                engineProvider.SetBlackExternalEngine(adapter);
        }
        else
        {
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
        engineType         = isRed ? saved.RedAiEngineType    : saved.BlackAiEngineType;
        searchDepth        = isRed ? saved.RedSearchDepth     : saved.BlackSearchDepth;
        searchTimeSeconds  = isRed ? saved.RedSearchTimeSeconds : saved.BlackSearchTimeSeconds;
        evaluatorType      = isRed ? saved.RedEvaluatorType   : saved.BlackEvaluatorType;
        nnueModelPath      = isRed ? saved.RedNnueModelPath   : saved.BlackNnueModelPath;

        // 還原選取的引擎 ID（新版）
        selectedEngineId = isRed ? saved.RedEngineId : saved.BlackEngineId;

        // 向後相容：舊版只有路徑，嘗試在 Registry 中比對
        if (selectedEngineId == null)
        {
            var legacyPath = isRed ? saved.RedEnginePath : saved.BlackEnginePath;
            if (!string.IsNullOrEmpty(legacyPath))
            {
                selectedEngineId = registry.Engines
                    .FirstOrDefault(e => string.Equals(
                        e.ExecutablePath, legacyPath, StringComparison.OrdinalIgnoreCase))
                    ?.Id;
            }
        }

        // 還原 Pikafish 設定
        var pf = isRed ? saved.RedPikafish : saved.BlackPikafish;
        RestorePikafishSettings(pf);

        NotifyAllPropertiesChanged();
    }

    /// <summary>將目前完整狀態快照至設定物件（供持久化使用）。</summary>
    public void CaptureToSettings(ExternalEngineSettings target)
    {
        bool isRed      = Color == PieceColor.Red;
        bool useExternal = engineType == AiEngineType.External;

        // 向後相容：同步寫入路徑欄位，方便舊版 code 讀取（新版優先讀 RedEngineId）
        var engineInfo = selectedEngineId != null ? registry.GetEngineInfo(selectedEngineId) : null;

        if (isRed)
        {
            target.RedAiEngineType       = engineType;
            target.RedSearchDepth        = searchDepth;
            target.RedSearchTimeSeconds  = searchTimeSeconds;
            target.RedEvaluatorType      = evaluatorType;
            target.RedNnueModelPath      = nnueModelPath;
            target.UseRedExternalEngine  = useExternal;
            target.RedEngineId           = selectedEngineId;
            target.RedEnginePath         = engineInfo?.ExecutablePath ?? string.Empty;
            target.RedProtocol           = engineInfo?.Protocol ?? EngineProtocol.Ucci;
            target.RedPikafish           = CapturePikafishSettings();
        }
        else
        {
            target.BlackAiEngineType      = engineType;
            target.BlackSearchDepth       = searchDepth;
            target.BlackSearchTimeSeconds = searchTimeSeconds;
            target.BlackEvaluatorType     = evaluatorType;
            target.BlackNnueModelPath     = nnueModelPath;
            target.UseBlackExternalEngine = useExternal;
            target.BlackEngineId          = selectedEngineId;
            target.BlackEnginePath        = engineInfo?.ExecutablePath ?? string.Empty;
            target.BlackProtocol          = engineInfo?.Protocol ?? EngineProtocol.Ucci;
            target.BlackPikafish          = CapturePikafishSettings();
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

    private async Task ApplyEngineAsync()
    {
        if (string.IsNullOrEmpty(selectedEngineId))
        {
            EngineStatus = "請先至 NNUE Tab 選擇引擎";
            return;
        }

        var activeAdapter = registry.GetActiveAdapter(selectedEngineId);
        if (activeAdapter == null)
        {
            EngineStatus = "引擎尚未連線，請至 NNUE Tab 重新載入";
            return;
        }

        adapter = activeAdapter;

        OnPropertyChanged(nameof(IsPikafish));
        OnPropertyChanged(nameof(UciEloEnabled));
        OnPropertyChanged(nameof(Rule60MaxPlyEnabled));

        var info = registry.GetEngineInfo(selectedEngineId);
        EngineStatus = $"已連接：{info?.DisplayName ?? activeAdapter.EngineName}";

        // 自動切換至外部引擎模式
        engineType = AiEngineType.External;
        OnPropertyChanged(nameof(EngineType));
        OnPropertyChanged(nameof(IsInternalEngine));
        OnPropertyChanged(nameof(IsExternalEngine));

        if (activeAdapter.IsPikafish)
            await ApplyPikafishSettingsAsync();

        SyncToService();
        SettingsChanged?.Invoke();
    }

    private void DisconnectEngine()
    {
        if (Color == PieceColor.Red)
            engineProvider.SetRedExternalEngine(null);
        else
            engineProvider.SetBlackExternalEngine(null);

        adapter = null;  // 不 Dispose，由 Registry 管理
        EngineStatus = "已中斷連線";
        OnPropertyChanged(nameof(IsPikafish));
        OnPropertyChanged(nameof(UciEloEnabled));
        OnPropertyChanged(nameof(Rule60MaxPlyEnabled));

        // 切換回內部引擎模式
        engineType = AiEngineType.Internal;
        OnPropertyChanged(nameof(EngineType));
        OnPropertyChanged(nameof(IsInternalEngine));
        OnPropertyChanged(nameof(IsExternalEngine));
        SyncToService();
        SettingsChanged?.Invoke();
    }

    private void OnRegistryEnginesChanged()
    {
        // EnginesChanged 可能由背景執行緒觸發，dispatch 至 UI 執行緒
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // 若選取的引擎已被移除，自動清除選取並斷線
            if (selectedEngineId != null && registry.GetEngineInfo(selectedEngineId) == null)
            {
                selectedEngineId = null;
                adapter = null;
                if (engineType == AiEngineType.External)
                {
                    engineType = AiEngineType.Internal;
                    SyncToService();
                }
                EngineStatus = "已選引擎已被卸載";
                NotifyAllPropertiesChanged();
            }
            else
            {
                OnPropertyChanged(nameof(AvailableEngines));
                OnPropertyChanged(nameof(HasNoEngines));
            }
        });
    }

    private async Task ApplyPikafishSettingsAsync()
    {
        if (adapter == null) return;
        await SendPikafishOptionsToAdapterAsync(adapter, CapturePikafishSettings());
    }

    private static async Task SendPikafishOptionsToAdapterAsync(IExternalEngineAdapter adpt, PikafishSettings s)
    {
        await adpt.SendOptionAsync("MultiPV", s.MultiPv.ToString());
        await adpt.SendOptionAsync("Skill Level", s.SkillLevel.ToString());
        await adpt.SendOptionAsync("UCI_LimitStrength", s.UciLimitStrength ? "true" : "false");
        await adpt.SendOptionAsync("UCI_Elo", s.UciElo.ToString());
        await adpt.SendOptionAsync("Sixty Move Rule", s.SixtyMoveRule ? "true" : "false");
        await adpt.SendOptionAsync("Rule60MaxPly", s.Rule60MaxPly.ToString());
        await adpt.SendOptionAsync("MateThreatDepth", s.MateThreatDepth.ToString());
        await adpt.SendOptionAsync("ScoreType", s.ScoreType.ToString());
        await adpt.SendOptionAsync("LU_Output", s.LuOutput ? "true" : "false");
        await adpt.SendOptionAsync("DrawRule", s.DrawRule.ToString());
        if (!string.IsNullOrWhiteSpace(s.EvalFile))
            await adpt.SendOptionAsync("EvalFile", s.EvalFile);
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
            Title  = $"選取{DisplayName} NNUE 模型檔",
            Filter = "NNUE 模型 (*.nnue)|*.nnue|所有檔案|*.*",
        };
        if (dialog.ShowDialog() == true)
            NnueModelPath = dialog.FileName;
    }

    private void BrowseEvalFile()
    {
        var dialog = new OpenFileDialog
        {
            Title  = $"選擇{DisplayName} EvalFile",
            Filter = "NNUE 評估檔|*.nnue|所有檔案|*.*"
        };
        if (dialog.ShowDialog() == true)
            EvalFile = dialog.FileName;
    }

    private PikafishSettings CapturePikafishSettings() => new()
    {
        MultiPv          = multiPv,
        SkillLevel       = skillLevel,
        UciLimitStrength = uciLimitStrength,
        UciElo           = uciElo,
        SixtyMoveRule    = sixtyMoveRule,
        Rule60MaxPly     = rule60MaxPly,
        MateThreatDepth  = mateThreatDepth,
        ScoreType        = scoreType,
        LuOutput         = luOutput,
        DrawRule         = drawRule,
        EvalFile         = evalFile
    };

    private void RestorePikafishSettings(PikafishSettings pf)
    {
        multiPv          = pf.MultiPv;
        skillLevel       = pf.SkillLevel;
        uciLimitStrength = pf.UciLimitStrength;
        uciElo           = pf.UciElo;
        sixtyMoveRule    = pf.SixtyMoveRule;
        rule60MaxPly     = pf.Rule60MaxPly;
        mateThreatDepth  = pf.MateThreatDepth;
        scoreType        = pf.ScoreType;
        luOutput         = pf.LuOutput;
        drawRule         = pf.DrawRule;
        evalFile         = pf.EvalFile;
    }

    /// <summary>通知所有屬性已變更（WPF 慣例：傳入空字串表示全部重新綁定）。</summary>
    private void NotifyAllPropertiesChanged() => OnPropertyChanged(string.Empty);

    /// <summary>啟動時自動連接已設定的外部引擎。</summary>
    public async Task AutoConnectIfNeededAsync()
    {
        if (engineType == AiEngineType.External && !string.IsNullOrEmpty(selectedEngineId))
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
            ModelFilePath  = nnueModelPath,
            EvaluationMode = NnueEvaluationMode.Composite,
        };
    }

    // ─── Dispose ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        registry.EnginesChanged -= OnRegistryEnginesChanged;
        adapter = null;  // adapter 由 Registry 管理，不在此 Dispose
    }
}
