using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.WPF.Core;

namespace ChineseChess.WPF.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly IGameService _gameService;

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
        _gameService = gameService;
        ChessBoard = new ChessBoardViewModel(gameService);
        ControlPanel = new ControlPanelViewModel(gameService);

        gameService.HintReady += hint => OnHintReady(hint);
        gameService.ThinkingProgress += progress => OnThinkingProgress(progress);

        // Listen to Service for Analysis updates if available
        // Ideally GameService should expose analysis events or properties
    }

    private void OnThinkingProgress(string progress)
    {
        if (global::System.Windows.Application.Current == null)
        {
            AnalysisText = progress;
            return;
        }

        global::System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            AnalysisText = progress;
        });
    }

    private void OnHintReady(SearchResult hint)
    {
        if (global::System.Windows.Application.Current == null)
        {
            AnalysisText = FormatHintText(hint);
            return;
        }

        global::System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            AnalysisText = FormatHintText(hint);
        });
    }

    private string FormatHintText(SearchResult hint)
    {
        if (hint.BestMove.IsNull)
        {
            return "提示：目前局面沒有可行的最佳走法";
        }

        var turnLabel = _gameService.CurrentBoard.Turn == PieceColor.Red ? "紅方" : "黑方";
        var scoreText = FormatScoreWithPerspective(hint.Score, turnLabel);

        return $"提示：{hint.BestMove} | 分數: {scoreText} | 深度: {hint.Depth} | 節點: {hint.Nodes}";
    }

    private static string FormatScoreWithPerspective(int score, string turnLabel)
    {
        string signedScore = score switch
        {
            > 0 => $"+{score}",
            < 0 => score.ToString(),
            _ => "0"
        };

        if (Math.Abs(score) >= 15000)
        {
            return $"{signedScore}（{turnLabel}，高分信號）";
        }

        return $"{signedScore}（{turnLabel}）";
    }
}
