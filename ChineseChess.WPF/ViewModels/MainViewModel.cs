using ChineseChess.Application.Interfaces;
using ChineseChess.WPF.Core;

namespace ChineseChess.WPF.ViewModels;

public class MainViewModel : ObservableObject
{
    public ChessBoardViewModel ChessBoard { get; }
    public ControlPanelViewModel ControlPanel { get; }
    
    // Analysis Panel Data
    private string _analysisText = "Waiting for analysis...";
    public string AnalysisText
    {
        get => _analysisText;
        set => SetProperty(ref _analysisText, value);
    }

    public MainViewModel(IGameService gameService)
    {
        ChessBoard = new ChessBoardViewModel(gameService);
        ControlPanel = new ControlPanelViewModel(gameService);
        
        // Listen to Service for Analysis updates if available
        // Ideally GameService should expose analysis events or properties
    }
}
