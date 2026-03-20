using ChineseChess.Application.Configuration;
using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Infrastructure.AI.Protocol;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// 外部引擎 / 雙協議伺服器設定的 ViewModel。
/// 對應 ExternalEngineView.xaml 的功能：
///   - 紅方 / 黑方可各自選擇 UCI 或 UCCI 外部引擎（.exe）
///   - 伺服器啟動 / 停止（同時接受 UCI 和 UCCI 客戶端）
/// </summary>
public class ExternalEngineViewModel : ObservableObject, IDisposable
{
    private readonly IEngineProvider engineProvider;
    private readonly IChessEngineServer engineServer;
    private readonly IUserSettingsService userSettingsService;

    // ─── 紅方外部引擎欄位 ─────────────────────────────────────────────────
    private bool useRedExternalEngine;
    private string redEnginePath = string.Empty;
    private EngineProtocol redProtocol = EngineProtocol.Ucci;
    private string redEngineStatus = "未啟用";

    // ─── 黑方外部引擎欄位 ─────────────────────────────────────────────────
    private bool useBlackExternalEngine;
    private string blackEnginePath = string.Empty;
    private EngineProtocol blackProtocol = EngineProtocol.Ucci;
    private string blackEngineStatus = "未啟用";

    // ─── Pikafish adapter 參照（不持有所有權，Dispose 由 EngineProvider 負責） ─
    private ExternalEngineAdapter? redAdapter;
    private ExternalEngineAdapter? blackAdapter;

    // ─── 紅方 Pikafish 設定欄位 ──────────────────────────────────────────
    private int redMultiPv = 1;
    private int redSkillLevel = 20;
    private bool redUciLimitStrength = false;
    private int redUciElo = 2850;
    private bool redSixtyMoveRule = true;
    private int redRule60MaxPly = 120;
    private int redMateThreatDepth = 0;
    private PikafishScoreType redScoreType = PikafishScoreType.Elo;
    private bool redLuOutput = true;
    private PikafishDrawRule redDrawRule = PikafishDrawRule.None;
    private string redEvalFile = string.Empty;

    // ─── 黑方 Pikafish 設定欄位 ──────────────────────────────────────────
    private int blackMultiPv = 1;
    private int blackSkillLevel = 20;
    private bool blackUciLimitStrength = false;
    private int blackUciElo = 2850;
    private bool blackSixtyMoveRule = true;
    private int blackRule60MaxPly = 120;
    private int blackMateThreatDepth = 0;
    private PikafishScoreType blackScoreType = PikafishScoreType.Elo;
    private bool blackLuOutput = true;
    private PikafishDrawRule blackDrawRule = PikafishDrawRule.None;
    private string blackEvalFile = string.Empty;

    // ─── 伺服器欄位 ───────────────────────────────────────────────────────
    private bool isServerRunning;
    private int serverPort = 23333;
    private string serverStatus = "伺服器已停止";

    // ─── 建構子 ───────────────────────────────────────────────────────────

    public ExternalEngineViewModel(IEngineProvider engineProvider, IChessEngineServer engineServer, IUserSettingsService userSettingsService)
    {
        this.engineProvider     = engineProvider     ?? throw new ArgumentNullException(nameof(engineProvider));
        this.engineServer       = engineServer       ?? throw new ArgumentNullException(nameof(engineServer));
        this.userSettingsService = userSettingsService ?? throw new ArgumentNullException(nameof(userSettingsService));

        this.engineServer.StatusChanged += OnServerStatusChanged;

        BrowseRedEngineCommand   = new RelayCommand(_ => BrowseEngine(isRed: true));
        BrowseBlackEngineCommand = new RelayCommand(_ => BrowseEngine(isRed: false));
        ApplyRedEngineCommand    = new RelayCommand(async _ => await ApplyEngineAsync(isRed: true));
        ApplyBlackEngineCommand  = new RelayCommand(async _ => await ApplyEngineAsync(isRed: false));
        StartServerCommand       = new RelayCommand(async _ => await StartServerAsync(), _ => !IsServerRunning);
        StopServerCommand        = new RelayCommand(async _ => await StopServerAsync(),  _ => IsServerRunning);

        ClearRedHashCommand      = new RelayCommand(async _ => await ClearHashAsync(isRed: true));
        ApplyRedPikafishCommand  = new RelayCommand(async _ => { await ApplyPikafishSettingsAsync(isRed: true); PersistCurrentSettings(); });
        BrowseRedEvalFileCommand = new RelayCommand(_ => BrowseEvalFile(isRed: true));
        ClearBlackHashCommand      = new RelayCommand(async _ => await ClearHashAsync(isRed: false));
        ApplyBlackPikafishCommand  = new RelayCommand(async _ => { await ApplyPikafishSettingsAsync(isRed: false); PersistCurrentSettings(); });
        BrowseBlackEvalFileCommand = new RelayCommand(_ => BrowseEvalFile(isRed: false));

        _ = LoadAndAutoConnectAsync().ContinueWith(
            t => System.Diagnostics.Trace.TraceWarning($"自動連接引擎失敗：{t.Exception?.InnerException?.Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // ─── 紅方屬性 ─────────────────────────────────────────────────────────

    public bool UseRedExternalEngine
    {
        get => useRedExternalEngine;
        set => SetProperty(ref useRedExternalEngine, value);
    }

    public string RedEnginePath
    {
        get => redEnginePath;
        set
        {
            if (SetProperty(ref redEnginePath, value))
                OnPropertyChanged(nameof(HasRedEngineConfig));
        }
    }

    public EngineProtocol RedProtocol
    {
        get => redProtocol;
        set => SetProperty(ref redProtocol, value);
    }

    public string RedEngineStatus
    {
        get => redEngineStatus;
        set => SetProperty(ref redEngineStatus, value);
    }

    // ─── 黑方屬性 ─────────────────────────────────────────────────────────

    public bool UseBlackExternalEngine
    {
        get => useBlackExternalEngine;
        set => SetProperty(ref useBlackExternalEngine, value);
    }

    public string BlackEnginePath
    {
        get => blackEnginePath;
        set
        {
            if (SetProperty(ref blackEnginePath, value))
                OnPropertyChanged(nameof(HasBlackEngineConfig));
        }
    }

    public EngineProtocol BlackProtocol
    {
        get => blackProtocol;
        set => SetProperty(ref blackProtocol, value);
    }

    public string BlackEngineStatus
    {
        get => blackEngineStatus;
        set => SetProperty(ref blackEngineStatus, value);
    }

    // ─── 伺服器屬性 ───────────────────────────────────────────────────────

    public bool IsServerRunning
    {
        get => isServerRunning;
        set
        {
            if (SetProperty(ref isServerRunning, value))
            {
                OnPropertyChanged(nameof(ServerButtonLabel));
                OnPropertyChanged(nameof(IsServerStopped));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int ServerPort
    {
        get => serverPort;
        set => SetProperty(ref serverPort, value);
    }

    public string ServerStatus
    {
        get => serverStatus;
        set => SetProperty(ref serverStatus, value);
    }

    public string ServerButtonLabel => IsServerRunning ? "停止伺服器" : "啟動伺服器";

    /// <summary>伺服器停止時為 true（供 IsEnabled 綁定使用）。</summary>
    public bool IsServerStopped => !IsServerRunning;

    // ─── 協議選項 ─────────────────────────────────────────────────────────

    public IEnumerable<EngineProtocol> EngineProtocols => Enum.GetValues<EngineProtocol>();

    // ─── Pikafish 偵測屬性（從 adapter 直接推導，無冗餘狀態） ───────────────

    public bool RedIsPikafish => redAdapter?.IsPikafish ?? false;
    public bool BlackIsPikafish => blackAdapter?.IsPikafish ?? false;

    // ─── 紅方 Pikafish 設定屬性 ───────────────────────────────────────────

    public int RedMultiPv
    {
        get => redMultiPv;
        set => SetProperty(ref redMultiPv, Math.Clamp(value, 1, 500));
    }

    public int RedSkillLevel
    {
        get => redSkillLevel;
        set => SetProperty(ref redSkillLevel, Math.Clamp(value, 0, 20));
    }

    public bool RedUciLimitStrength
    {
        get => redUciLimitStrength;
        set
        {
            if (SetProperty(ref redUciLimitStrength, value))
                OnPropertyChanged(nameof(RedUciEloEnabled));
        }
    }

    public int RedUciElo
    {
        get => redUciElo;
        set => SetProperty(ref redUciElo, Math.Clamp(value, 1280, 3133));
    }

    public bool RedSixtyMoveRule
    {
        get => redSixtyMoveRule;
        set
        {
            if (SetProperty(ref redSixtyMoveRule, value))
                OnPropertyChanged(nameof(RedRule60MaxPlyEnabled));
        }
    }

    public int RedRule60MaxPly
    {
        get => redRule60MaxPly;
        set => SetProperty(ref redRule60MaxPly, Math.Clamp(value, 1, 150));
    }

    public int RedMateThreatDepth
    {
        get => redMateThreatDepth;
        set => SetProperty(ref redMateThreatDepth, Math.Clamp(value, 0, 10));
    }

    public PikafishScoreType RedScoreType
    {
        get => redScoreType;
        set => SetProperty(ref redScoreType, value);
    }

    public bool RedLuOutput
    {
        get => redLuOutput;
        set => SetProperty(ref redLuOutput, value);
    }

    public PikafishDrawRule RedDrawRule
    {
        get => redDrawRule;
        set => SetProperty(ref redDrawRule, value);
    }

    public string RedEvalFile
    {
        get => redEvalFile;
        set => SetProperty(ref redEvalFile, value);
    }

    public bool RedUciEloEnabled => RedIsPikafish && RedUciLimitStrength;
    public bool RedRule60MaxPlyEnabled => RedIsPikafish && RedSixtyMoveRule;

    // ─── 黑方 Pikafish 設定屬性 ───────────────────────────────────────────

    public int BlackMultiPv
    {
        get => blackMultiPv;
        set => SetProperty(ref blackMultiPv, Math.Clamp(value, 1, 500));
    }

    public int BlackSkillLevel
    {
        get => blackSkillLevel;
        set => SetProperty(ref blackSkillLevel, Math.Clamp(value, 0, 20));
    }

    public bool BlackUciLimitStrength
    {
        get => blackUciLimitStrength;
        set
        {
            if (SetProperty(ref blackUciLimitStrength, value))
                OnPropertyChanged(nameof(BlackUciEloEnabled));
        }
    }

    public int BlackUciElo
    {
        get => blackUciElo;
        set => SetProperty(ref blackUciElo, Math.Clamp(value, 1280, 3133));
    }

    public bool BlackSixtyMoveRule
    {
        get => blackSixtyMoveRule;
        set
        {
            if (SetProperty(ref blackSixtyMoveRule, value))
                OnPropertyChanged(nameof(BlackRule60MaxPlyEnabled));
        }
    }

    public int BlackRule60MaxPly
    {
        get => blackRule60MaxPly;
        set => SetProperty(ref blackRule60MaxPly, Math.Clamp(value, 1, 150));
    }

    public int BlackMateThreatDepth
    {
        get => blackMateThreatDepth;
        set => SetProperty(ref blackMateThreatDepth, Math.Clamp(value, 0, 10));
    }

    public PikafishScoreType BlackScoreType
    {
        get => blackScoreType;
        set => SetProperty(ref blackScoreType, value);
    }

    public bool BlackLuOutput
    {
        get => blackLuOutput;
        set => SetProperty(ref blackLuOutput, value);
    }

    public PikafishDrawRule BlackDrawRule
    {
        get => blackDrawRule;
        set => SetProperty(ref blackDrawRule, value);
    }

    public string BlackEvalFile
    {
        get => blackEvalFile;
        set => SetProperty(ref blackEvalFile, value);
    }

    public bool BlackUciEloEnabled => BlackIsPikafish && BlackUciLimitStrength;
    public bool BlackRule60MaxPlyEnabled => BlackIsPikafish && BlackSixtyMoveRule;

    // ─── Pikafish ComboBox 來源 ───────────────────────────────────────────

    public IEnumerable<PikafishScoreType> ScoreTypes => Enum.GetValues<PikafishScoreType>();
    public IEnumerable<PikafishDrawRule> DrawRules => Enum.GetValues<PikafishDrawRule>();

    // ─── 供設定頁快速開關使用的 API ──────────────────────────────────────

    /// <summary>紅方已設定引擎路徑（供設定頁 CheckBox IsEnabled 綁定）。</summary>
    public bool HasRedEngineConfig => !string.IsNullOrWhiteSpace(RedEnginePath);

    /// <summary>黑方已設定引擎路徑（供設定頁 CheckBox IsEnabled 綁定）。</summary>
    public bool HasBlackEngineConfig => !string.IsNullOrWhiteSpace(BlackEnginePath);

    // ─── 命令 ─────────────────────────────────────────────────────────────

    public ICommand BrowseRedEngineCommand   { get; }
    public ICommand BrowseBlackEngineCommand { get; }
    public ICommand ApplyRedEngineCommand    { get; }
    public ICommand ApplyBlackEngineCommand  { get; }
    public ICommand StartServerCommand       { get; }
    public ICommand StopServerCommand        { get; }

    // ─── Pikafish 命令 ────────────────────────────────────────────────────

    public ICommand ClearRedHashCommand        { get; }
    public ICommand ApplyRedPikafishCommand    { get; }
    public ICommand BrowseRedEvalFileCommand   { get; }
    public ICommand ClearBlackHashCommand      { get; }
    public ICommand ApplyBlackPikafishCommand  { get; }
    public ICommand BrowseBlackEvalFileCommand { get; }

    // ─── 私有命令實作 ─────────────────────────────────────────────────────

    private void BrowseEngine(bool isRed)
    {
        var dialog = new OpenFileDialog
        {
            Title  = isRed ? "選擇紅方引擎" : "選擇黑方引擎",
            Filter = "可執行檔|*.exe|所有檔案|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        if (isRed)
            RedEnginePath = dialog.FileName;
        else
            BlackEnginePath = dialog.FileName;
    }

    private async Task ApplyEngineAsync(bool isRed)
    {
        var path     = isRed ? RedEnginePath     : BlackEnginePath;
        var protocol = isRed ? RedProtocol       : BlackProtocol;
        var useExt   = isRed ? UseRedExternalEngine : UseBlackExternalEngine;

        if (!useExt)
        {
            // 停用外部引擎，恢復內建
            if (isRed)
            {
                engineProvider.SetRedExternalEngine(null);
                RedEngineStatus = "已恢復內建引擎";
                redAdapter = null;
                OnPropertyChanged(nameof(RedIsPikafish));
                OnPropertyChanged(nameof(RedUciEloEnabled));
                OnPropertyChanged(nameof(RedRule60MaxPlyEnabled));
            }
            else
            {
                engineProvider.SetBlackExternalEngine(null);
                BlackEngineStatus = "已恢復內建引擎";
                blackAdapter = null;
                OnPropertyChanged(nameof(BlackIsPikafish));
                OnPropertyChanged(nameof(BlackUciEloEnabled));
                OnPropertyChanged(nameof(BlackRule60MaxPlyEnabled));
            }
            PersistCurrentSettings();
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            if (isRed) RedEngineStatus   = "請先選擇引擎執行檔";
            else       BlackEngineStatus = "請先選擇引擎執行檔";
            return;
        }

        if (isRed) RedEngineStatus   = "初始化中...";
        else       BlackEngineStatus = "初始化中...";

        var adapter = new ExternalEngineAdapter(path, protocol);
        try
        {
            using var cts = new CancellationTokenSource(10_000); // 10 秒超時
            await adapter.InitializeAsync(cts.Token);

            if (isRed)
            {
                engineProvider.SetRedExternalEngine(adapter);
                redAdapter = adapter;
                OnPropertyChanged(nameof(RedIsPikafish));
                OnPropertyChanged(nameof(RedUciEloEnabled));
                OnPropertyChanged(nameof(RedRule60MaxPlyEnabled));
                RedEngineStatus = adapter.IsPikafish
                    ? $"已載入（{adapter.EngineName}）"
                    : $"已載入（{protocol}）";
                if (adapter.IsPikafish) await ApplyPikafishSettingsAsync(isRed: true);
            }
            else
            {
                engineProvider.SetBlackExternalEngine(adapter);
                blackAdapter = adapter;
                OnPropertyChanged(nameof(BlackIsPikafish));
                OnPropertyChanged(nameof(BlackUciEloEnabled));
                OnPropertyChanged(nameof(BlackRule60MaxPlyEnabled));
                BlackEngineStatus = adapter.IsPikafish
                    ? $"已載入（{adapter.EngineName}）"
                    : $"已載入（{protocol}）";
                if (adapter.IsPikafish) await ApplyPikafishSettingsAsync(isRed: false);
            }

            PersistCurrentSettings();
        }
        catch (Exception ex)
        {
            adapter.Dispose();
            if (isRed) RedEngineStatus   = $"初始化失敗：{ex.Message}";
            else       BlackEngineStatus = $"初始化失敗：{ex.Message}";
        }
    }

    /// <summary>
    /// 將目前引擎設定快照寫入持久化儲存。
    /// </summary>
    private void PersistCurrentSettings()
    {
        var snapshot = new ExternalEngineSettings
        {
            UseRedExternalEngine   = UseRedExternalEngine,
            RedEnginePath          = RedEnginePath,
            RedProtocol            = RedProtocol,
            UseBlackExternalEngine = UseBlackExternalEngine,
            BlackEnginePath        = BlackEnginePath,
            BlackProtocol          = BlackProtocol,
            ServerPort             = ServerPort,
            RedPikafish   = CapturePikafishSettings(isRed: true),
            BlackPikafish = CapturePikafishSettings(isRed: false)
        };
        userSettingsService.SaveEngineSettings(snapshot);
    }

    /// <summary>
    /// 從設定頁快速切換外部引擎（回傳 false 代表尚未設定路徑）。
    /// </summary>
    public async Task<bool> ToggleEngineAsync(bool isRed, bool enable)
    {
        var path = isRed ? RedEnginePath : BlackEnginePath;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (isRed) UseRedExternalEngine   = enable;
        else       UseBlackExternalEngine = enable;

        await ApplyEngineAsync(isRed);
        return true;
    }

    /// <summary>
    /// 啟動時載入持久化設定，並自動連接已設定的外部引擎。
    /// </summary>
    private async Task LoadAndAutoConnectAsync()
    {
        try
        {
            var saved = userSettingsService.LoadEngineSettings();

            // 在 UI 執行緒還原欄位，ConfigureAwait(false) 確保後續引擎初始化不佔用 UI 執行緒
            var app = System.Windows.Application.Current;
            if (app != null)
                await app.Dispatcher.InvokeAsync(() => RestoreSettings(saved)).Task.ConfigureAwait(false);
            else
                RestoreSettings(saved);

            // 自動連接（在背景執行緒執行，不阻塞 UI）
            if (saved.UseRedExternalEngine && !string.IsNullOrWhiteSpace(saved.RedEnginePath))
                await ApplyEngineAsync(isRed: true);

            if (saved.UseBlackExternalEngine && !string.IsNullOrWhiteSpace(saved.BlackEnginePath))
                await ApplyEngineAsync(isRed: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"自動載入引擎設定失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// 將持久化設定還原到 backing fields，並通知 UI 屬性變更（必須在 UI 執行緒呼叫）。
    /// </summary>
    private void RestoreSettings(ExternalEngineSettings saved)
    {
        redEnginePath          = saved.RedEnginePath;
        redProtocol            = saved.RedProtocol;
        useRedExternalEngine   = saved.UseRedExternalEngine;
        blackEnginePath        = saved.BlackEnginePath;
        blackProtocol          = saved.BlackProtocol;
        useBlackExternalEngine = saved.UseBlackExternalEngine;
        serverPort             = saved.ServerPort;

        // 還原紅方 Pikafish 設定
        var rp = saved.RedPikafish;
        redMultiPv          = rp.MultiPv;
        redSkillLevel       = rp.SkillLevel;
        redUciLimitStrength = rp.UciLimitStrength;
        redUciElo           = rp.UciElo;
        redSixtyMoveRule    = rp.SixtyMoveRule;
        redRule60MaxPly     = rp.Rule60MaxPly;
        redMateThreatDepth  = rp.MateThreatDepth;
        redScoreType        = rp.ScoreType;
        redLuOutput         = rp.LuOutput;
        redDrawRule         = rp.DrawRule;
        redEvalFile         = rp.EvalFile;

        // 還原黑方 Pikafish 設定
        var bp = saved.BlackPikafish;
        blackMultiPv          = bp.MultiPv;
        blackSkillLevel       = bp.SkillLevel;
        blackUciLimitStrength = bp.UciLimitStrength;
        blackUciElo           = bp.UciElo;
        blackSixtyMoveRule    = bp.SixtyMoveRule;
        blackRule60MaxPly     = bp.Rule60MaxPly;
        blackMateThreatDepth  = bp.MateThreatDepth;
        blackScoreType        = bp.ScoreType;
        blackLuOutput         = bp.LuOutput;
        blackDrawRule         = bp.DrawRule;
        blackEvalFile         = bp.EvalFile;

        OnPropertyChanged(nameof(RedEnginePath));
        OnPropertyChanged(nameof(RedProtocol));
        OnPropertyChanged(nameof(UseRedExternalEngine));
        OnPropertyChanged(nameof(BlackEnginePath));
        OnPropertyChanged(nameof(BlackProtocol));
        OnPropertyChanged(nameof(UseBlackExternalEngine));
        OnPropertyChanged(nameof(ServerPort));
        OnPropertyChanged(nameof(HasRedEngineConfig));
        OnPropertyChanged(nameof(HasBlackEngineConfig));

        // 通知 Pikafish 設定屬性變更
        OnPropertyChanged(nameof(RedMultiPv));
        OnPropertyChanged(nameof(RedSkillLevel));
        OnPropertyChanged(nameof(RedUciLimitStrength));
        OnPropertyChanged(nameof(RedUciElo));
        OnPropertyChanged(nameof(RedSixtyMoveRule));
        OnPropertyChanged(nameof(RedRule60MaxPly));
        OnPropertyChanged(nameof(RedMateThreatDepth));
        OnPropertyChanged(nameof(RedScoreType));
        OnPropertyChanged(nameof(RedLuOutput));
        OnPropertyChanged(nameof(RedDrawRule));
        OnPropertyChanged(nameof(RedEvalFile));
        OnPropertyChanged(nameof(BlackMultiPv));
        OnPropertyChanged(nameof(BlackSkillLevel));
        OnPropertyChanged(nameof(BlackUciLimitStrength));
        OnPropertyChanged(nameof(BlackUciElo));
        OnPropertyChanged(nameof(BlackSixtyMoveRule));
        OnPropertyChanged(nameof(BlackRule60MaxPly));
        OnPropertyChanged(nameof(BlackMateThreatDepth));
        OnPropertyChanged(nameof(BlackScoreType));
        OnPropertyChanged(nameof(BlackLuOutput));
        OnPropertyChanged(nameof(BlackDrawRule));
        OnPropertyChanged(nameof(BlackEvalFile));
    }

    // ─── Pikafish 私有方法 ────────────────────────────────────────────────

    /// <summary>
    /// 將目前 UI 上的 Pikafish 設定發送至引擎（依序 setoption）。
    /// 呼叫端負責在需要時呼叫 PersistCurrentSettings()。
    /// </summary>
    private async Task ApplyPikafishSettingsAsync(bool isRed)
    {
        var adapter = isRed ? redAdapter : blackAdapter;
        if (adapter == null) return;
        await SendPikafishOptionsToAdapterAsync(adapter, CapturePikafishSettings(isRed));
    }

    /// <summary>依序向引擎發送 11 個 Pikafish setoption 命令。</summary>
    private static async Task SendPikafishOptionsToAdapterAsync(ExternalEngineAdapter adapter, PikafishSettings s)
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

    /// <summary>從目前 ViewModel 屬性建立 PikafishSettings 快照。</summary>
    private PikafishSettings CapturePikafishSettings(bool isRed) => isRed
        ? new PikafishSettings
        {
            MultiPv          = RedMultiPv,
            SkillLevel       = RedSkillLevel,
            UciLimitStrength = RedUciLimitStrength,
            UciElo           = RedUciElo,
            SixtyMoveRule    = RedSixtyMoveRule,
            Rule60MaxPly     = RedRule60MaxPly,
            MateThreatDepth  = RedMateThreatDepth,
            ScoreType        = RedScoreType,
            LuOutput         = RedLuOutput,
            DrawRule         = RedDrawRule,
            EvalFile         = RedEvalFile
        }
        : new PikafishSettings
        {
            MultiPv          = BlackMultiPv,
            SkillLevel       = BlackSkillLevel,
            UciLimitStrength = BlackUciLimitStrength,
            UciElo           = BlackUciElo,
            SixtyMoveRule    = BlackSixtyMoveRule,
            Rule60MaxPly     = BlackRule60MaxPly,
            MateThreatDepth  = BlackMateThreatDepth,
            ScoreType        = BlackScoreType,
            LuOutput         = BlackLuOutput,
            DrawRule         = BlackDrawRule,
            EvalFile         = BlackEvalFile
        };

    private async Task ClearHashAsync(bool isRed)
    {
        var adapter = isRed ? redAdapter : blackAdapter;
        if (adapter == null) return;
        await adapter.SendButtonOptionAsync("Clear Hash");
    }

    private void BrowseEvalFile(bool isRed)
    {
        var dialog = new OpenFileDialog
        {
            Title  = isRed ? "選擇紅方 EvalFile" : "選擇黑方 EvalFile",
            Filter = "NNUE 評估檔|*.nnue|所有檔案|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        if (isRed)
            RedEvalFile = dialog.FileName;
        else
            BlackEvalFile = dialog.FileName;
    }

    private async Task StartServerAsync()
    {
        try
        {
            await engineServer.StartAsync(ServerPort);
            IsServerRunning = true;
        }
        catch (Exception ex)
        {
            ServerStatus = $"啟動失敗：{ex.Message}";
        }
    }

    private async Task StopServerAsync()
    {
        try
        {
            await engineServer.StopAsync();
            IsServerRunning = false;
        }
        catch (Exception ex)
        {
            ServerStatus = $"停止失敗：{ex.Message}";
        }
    }

    private void OnServerStatusChanged(string message)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ServerStatus    = message;
            IsServerRunning = engineServer.IsRunning;
        });
    }

    // ─── Dispose ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        engineServer.StatusChanged -= OnServerStatusChanged;
    }
}
