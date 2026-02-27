using ChineseChess.Application.Interfaces;
using ChineseChess.Application.Services;
using ChineseChess.Infrastructure.AI.Search;
using ChineseChess.WPF.ViewModels;
using MainWindowView = ChineseChess.WPF.Views.MainWindow;
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
        var services = new ServiceCollection();

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
