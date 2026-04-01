using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Infrastructure.AI.Analysis;
using ChineseChess.Infrastructure.AI.Book;
using ChineseChess.Infrastructure.AI.Hint;
using ChineseChess.Infrastructure.AI.Nnue;
using ChineseChess.Infrastructure.AI.Nnue.Evaluator;
using ChineseChess.Infrastructure.AI.Nnue.Network;
using ChineseChess.Infrastructure.AI.Protocol;
using ChineseChess.Infrastructure.AI.Search;
using ChineseChess.Infrastructure.Persistence;
using ChineseChess.Infrastructure.Tablebase;
using ChineseChess.WPF.ViewModels;
using MainWindowView = ChineseChess.WPF.Views.MainWindow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using System.Windows;
using ChineseChess.Application.Enums;

namespace ChineseChess.WPF;

public partial class App : System.Windows.Application
{
    public IServiceProvider Services { get; }

    public new static App Current => (App)System.Windows.Application.Current;

    public App()
    {
        Services = ConfigureServices();
    }

    private static IServiceProvider ConfigureServices()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var gameSettings = new GameSettings();
        config.GetSection("GameSettings").Bind(gameSettings);
        var hintExplanationSettings = new HintExplanationSettings();
        config.GetSection("HintExplanation").Bind(hintExplanationSettings);
        var gameAnalysisSettings = new GameAnalysisSettings();
        config.GetSection("GameAnalysis").Bind(gameAnalysisSettings);
        var openingBookSettings = new OpeningBookSettings();
        config.GetSection("OpeningBook").Bind(openingBookSettings);

        var services = new ServiceCollection();

        services.AddSingleton(gameSettings);
        services.AddSingleton(hintExplanationSettings);
        services.AddSingleton(gameAnalysisSettings);
        services.AddSingleton(openingBookSettings);
        services.AddSingleton<IHintExplanationService>(_ => new OpenAICompatibleHintExplanationService(hintExplanationSettings));
        services.AddSingleton<IGameAnalysisService>(_ => new GameAnalysisService(gameAnalysisSettings));

        // 開局庫（啟動時從預設資料建構，若設定檔存在則改從檔案載入）
        services.AddSingleton<IOpeningBook>(_ => BuildOpeningBook(openingBookSettings));

        // 基礎設施（Infrastructure）— NNUE
        services.AddSingleton<INnueSettingsService, JsonNnueSettingsService>();
        services.AddSingleton<INnueNetwork, NnueNetwork>();
        // CompositeEvaluator：NNUE 已載入時使用 NNUE，否則 fallback 至 HandcraftedEvaluator
        services.AddSingleton<ChineseChess.Infrastructure.AI.Evaluators.IEvaluator>(sp =>
            new CompositeEvaluator(sp.GetRequiredService<INnueNetwork>()));

        // SearchEngine：底層引擎（不含開局庫），供 ChessEngineServer 直接使用
        services.AddSingleton<SearchEngine>(sp =>
            new SearchEngine(
                sp.GetRequiredService<GameSettings>(),
                sp.GetRequiredService<ChineseChess.Infrastructure.AI.Evaluators.IEvaluator>()));
        // IAiEngine：包裝開局庫的 Decorator，供 GameService / EngineProvider 使用
        services.AddSingleton<IAiEngine>(sp =>
            new OpeningBookEngineDecorator(
                sp.GetRequiredService<SearchEngine>(),
                sp.GetRequiredService<IOpeningBook>(),
                sp.GetRequiredService<OpeningBookSettings>()));
        services.AddSingleton<IChessEngineServer>(sp =>
            new ChessEngineServer(sp.GetRequiredService<SearchEngine>()));
        services.AddSingleton<IUserSettingsService, JsonUserSettingsService>();

        // NNUE 引擎工廠（Infrastructure → Application 介面的橋接）
        services.AddSingleton<IAiEngineFactory>(sp =>
            new NnueAiEngineFactory(
                sp.GetRequiredService<GameSettings>(),
                sp.GetRequiredService<IOpeningBook>(),
                sp.GetRequiredService<OpeningBookSettings>()));

        // 應用層（Application）
        services.AddSingleton<IEngineProvider>(sp =>
            new EngineProvider(
                sp.GetRequiredService<IAiEngine>(),
                sp.GetRequiredService<IAiEngineFactory>()));
        // 殘局庫服務（需在 IGameService 之前注冊，因後者以 GetRequiredService 取用）
        services.AddSingleton<ITablebaseService, TablebaseService>();
        services.AddSingleton<IGameService>(sp => new GameService(
            sp.GetRequiredService<IAiEngine>(),
            sp.GetService<IHintExplanationService>(),
            sp.GetRequiredService<IEngineProvider>(),
            sp.GetRequiredService<ITablebaseService>()));
        services.AddSingleton<ICoreGameService>(sp => sp.GetRequiredService<IGameService>());
        services.AddSingleton<IGameRecordService, GameRecordService>();

        // ViewModel 層
        services.AddTransient<MainViewModel>();
        services.AddTransient<ChessBoardViewModel>();
        services.AddSingleton<ExternalEngineViewModel>(sp =>
            new ExternalEngineViewModel(
                sp.GetRequiredService<IChessEngineServer>(),
                sp.GetRequiredService<IUserSettingsService>()));
        services.AddTransient<MoveHistoryViewModel>(sp => new MoveHistoryViewModel(
            sp.GetRequiredService<IGameService>(),
            sp.GetRequiredService<IGameRecordService>()));
        // NnueTrainingViewModel 以 Singleton 登記；Lazy<> 確保僅在首次存取時才初始化，
        // 避免預先佔用 TrainingNetwork 的 ~200MB 記憶體。
        // 必須使用 Singleton（非 Transient），防止 Singleton NnueViewModel 的 Lazy<> 欄位
        // 每次解析到不同實例，導致 IDisposable 逸出（Captive Dependency 問題）。
        services.AddSingleton<NnueTrainingViewModel>();
        services.AddSingleton<NnueViewModel>(sp =>
            new NnueViewModel(
                sp.GetRequiredService<INnueNetwork>(),
                sp.GetRequiredService<INnueSettingsService>(),
                sp.GetRequiredService<IEngineProvider>(),
                new Lazy<NnueTrainingViewModel>(() => sp.GetRequiredService<NnueTrainingViewModel>())));
        services.AddSingleton<EndgameTablebViewModel>(sp =>
            new EndgameTablebViewModel(
                sp.GetRequiredService<ITablebaseService>(),
                sp.GetRequiredService<IGameService>()));

        // Elo 評估服務（純邏輯，無外部依賴）
        services.AddSingleton<EloMatchService>();

        // EloMatchViewModel：Singleton，確保評估狀態在切換 Tab 時不遺失
        services.AddSingleton<EloMatchViewModel>(sp =>
            new EloMatchViewModel(
                sp.GetRequiredService<IAiEngine>(),
                sp.GetRequiredService<IAiEngineFactory>(),
                sp.GetRequiredService<IEngineProvider>(),
                sp.GetRequiredService<EloMatchService>()));

        // Per-player AI 設定 ViewModel（紅方 / 黑方各一個實例，透過 Holder 包裝以便 DI 解析）
        services.AddSingleton(sp =>
        {
            var gameService = sp.GetRequiredService<IGameService>();
            var engineProvider = sp.GetRequiredService<IEngineProvider>();
            var engineFactory = sp.GetRequiredService<IAiEngineFactory>();
            return new AiPlayerSettingsHolder(
                new AiPlayerSettingsViewModel(
                    Domain.Enums.PieceColor.Red, gameService, engineProvider, engineFactory),
                new AiPlayerSettingsViewModel(
                    Domain.Enums.PieceColor.Black, gameService, engineProvider, engineFactory));
        });

        services.AddTransient<ControlPanelViewModel>(sp =>
        {
            var aiHolder = sp.GetRequiredService<AiPlayerSettingsHolder>();
            return new ControlPanelViewModel(
                sp.GetRequiredService<IGameService>(),
                sp.GetRequiredService<GameSettings>(),
                sp.GetService<IGameAnalysisService>(),
                sp.GetService<GameAnalysisSettings>(),
                sp.GetRequiredService<ExternalEngineViewModel>(),
                sp.GetRequiredService<MoveHistoryViewModel>(),
                sp.GetRequiredService<NnueViewModel>(),
                sp.GetRequiredService<EndgameTablebViewModel>(),
                sp.GetRequiredService<EloMatchViewModel>(),
                aiHolder.Red,
                aiHolder.Black,
                sp.GetRequiredService<IUserSettingsService>());
        });

        // 視圖層（Views）
        services.AddTransient<MainWindowView>();

        return services.BuildServiceProvider();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = Services.GetRequiredService<MainWindowView>();
        mainWindow.DataContext = Services.GetRequiredService<MainViewModel>();
        mainWindow.Show();
    }

    /// <summary>
    /// 建構開局庫：若設定的 bin 檔案存在則從檔案載入；否則使用內建預設資料。
    /// </summary>
    private static IOpeningBook BuildOpeningBook(OpeningBookSettings settings)
    {
        if (settings.IsEnabled && System.IO.File.Exists(settings.BookFilePath))
        {
            try
            {
                using var fs = System.IO.File.OpenRead(settings.BookFilePath);
                var bookFromFile = OpeningBookSerializer.LoadFromBinary(fs, settings.UseRandomSelection);
                if (bookFromFile.IsLoaded) return bookFromFile;
            }
            catch (Exception ex)
            {
                // 檔案損壞時退化為內建預設資料，並記錄錯誤供診斷
                Trace.TraceWarning($"開局庫檔案 '{settings.BookFilePath}' 載入失敗，退化為預設資料：{ex.Message}");
            }
        }
        return DefaultOpeningData.Build();
    }
}
