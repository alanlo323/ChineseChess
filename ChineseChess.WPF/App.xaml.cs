using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Infrastructure.AI.Analysis;
using ChineseChess.Infrastructure.AI.Book;
using ChineseChess.Infrastructure.AI.Hint;
using ChineseChess.Infrastructure.AI.Search;
using ChineseChess.WPF.ViewModels;
using MainWindowView = ChineseChess.WPF.Views.MainWindow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using System.Windows;

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

        // 基礎設施（Infrastructure）
        services.AddSingleton<IAiEngine, SearchEngine>();

        // 應用層（Application）
        services.AddSingleton<IGameService, GameService>();

        // ViewModel 層
        services.AddTransient<MainViewModel>();
        services.AddTransient<ChessBoardViewModel>();
        services.AddTransient<ControlPanelViewModel>();

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
