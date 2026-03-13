using ChineseChess.WPF.ViewModels;
using System.Windows;

namespace ChineseChess.WPF.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, System.EventArgs e)
    {
        if (DataContext is MainViewModel mainViewModel)
        {
            mainViewModel.ControlPanel.Dispose();
        }
    }
}
