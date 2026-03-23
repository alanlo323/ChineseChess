using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.Domain.Helpers;
using ChineseChess.WPF.Core;
using System;

namespace ChineseChess.WPF.ViewModels;

public class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGameService gameService;

    public ChessBoardViewModel ChessBoard { get; }
    public ControlPanelViewModel ControlPanel { get; }

    // 分析面板資料
    private string analysisText = "Waiting for analysis...";
    public string AnalysisText
    {
        get => analysisText;
        set => SetProperty(ref analysisText, value);
    }

    public MainViewModel(IGameService gameService, ChessBoardViewModel chessBoard, ControlPanelViewModel controlPanel)
    {
        this.gameService = gameService;
        ChessBoard = chessBoard;
        ControlPanel = controlPanel;

        gameService.HintReady += OnHintReady;
        gameService.ThinkingProgress += OnThinkingProgress;

        // MultiPV 走法選取橋接：ControlPanel → ChessBoard
        controlPanel.MultiPvMoveSelected += chessBoard.SelectMultiPvMove;
    }

    private void OnThinkingProgress(string progress)
    {
        if (global::System.Windows.Application.Current == null)
        {
            AnalysisText = progress;
            return;
        }

        global::System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
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

        global::System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
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

        var turnLabel = gameService.CurrentBoard.Turn == PieceColor.Red ? "紅方" : "黑方";
        var scoreText = FormatScoreWithPerspective(hint.Score, turnLabel);

        var notation = MoveNotation.ToNotation(hint.BestMove, gameService.CurrentBoard);
        return $"提示：{notation} | 分數: {scoreText} | 深度: {hint.Depth} | 節點: {hint.Nodes:N0}";
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

    public void Dispose()
    {
        // 取消訂閱 GameService 事件，防止 ViewModel 被 Service 持有引用而無法被 GC
        gameService.HintReady -= OnHintReady;
        gameService.ThinkingProgress -= OnThinkingProgress;
        ControlPanel.MultiPvMoveSelected -= ChessBoard.SelectMultiPvMove;
    }
}
