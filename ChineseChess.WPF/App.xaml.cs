using ChineseChess.Application.Configuration;
using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Infrastructure.AI.Hint;
using ChineseChess.Infrastructure.AI.Search;
using ChineseChess.WPF.ViewModels;
using MainWindowView = ChineseChess.WPF.Views.MainWindow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
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

        var services = new ServiceCollection();

        services.AddSingleton(gameSettings);
        services.AddSingleton(hintExplanationSettings);
        services.AddSingleton<IHintExplanationService>(_ => new OpenAICompatibleHintExplanationService(hintExplanationSettings));

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
}
