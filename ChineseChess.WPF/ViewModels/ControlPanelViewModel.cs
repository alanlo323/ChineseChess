using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Enums;
using ChineseChess.WPF.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

public class ControlPanelViewModel : ObservableObject
{
    private readonly IGameService _gameService;
    private GameMode _selectedMode = GameMode.PlayerVsAi;
    private string _statusMessage = "Ready";
    private int _searchDepth = 5;
    private int _searchThinkingTime = 3;

    public IEnumerable<GameMode> GameModes => Enum.GetValues<GameMode>();

    public GameMode SelectedMode
    {
        get => _selectedMode;
        set => SetProperty(ref _selectedMode, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }
    
    public int SearchDepth
    {
        get => _searchDepth;
        set
        {
            if (SetProperty(ref _searchDepth, value))
            {
                _gameService.SetDifficulty(value, _searchThinkingTime * 1000);
            }
        }
    }

    public int SearchThinkingTime
    {
        get => _searchThinkingTime;
        set
        {
            if (SetProperty(ref _searchThinkingTime, value))
            {
                _gameService.SetDifficulty(_searchDepth, value * 1000);
            }
        }
    }

    public ICommand StartGameCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand HintCommand { get; }
    public ICommand StopThinkingCommand { get; }
    public ICommand PauseThinkingCommand { get; }
    public ICommand ResumeThinkingCommand { get; }
    public ICommand ExportTranspositionTableCommand { get; }
    public ICommand ImportTranspositionTableCommand { get; }

    public ControlPanelViewModel(IGameService gameService)
    {
        _gameService = gameService;
        StartGameCommand = new RelayCommand(async _ => await _gameService.StartGameAsync(SelectedMode));
        UndoCommand = new RelayCommand(_ => _gameService.Undo());
        StopThinkingCommand = new RelayCommand(async _ =>
        {
            StatusMessage = "停止思考中...";
            await _gameService.StopGameAsync();
            StatusMessage = "AI 思考已停止";
        });
        PauseThinkingCommand = new RelayCommand(async _ =>
        {
            StatusMessage = "正在暫停思考...";
            await _gameService.PauseThinkingAsync();
        });
        ResumeThinkingCommand = new RelayCommand(async _ =>
        {
            StatusMessage = "繼續思考中...";
            await _gameService.ResumeThinkingAsync();
        });
        ExportTranspositionTableCommand = new RelayCommand(async _ =>
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "匯出 TT 表",
                    Filter = "Chinese Chess TT|*.cctt;*.json|Binary|*.cctt|JSON|*.json|All files|*.*",
                    FileName = $"transposition-table-{DateTime.Now:yyyyMMdd_HHmmss}.cctt"
                };

                if (dialog.ShowDialog() != true) return;

                var asJson = string.Equals(
                    Path.GetExtension(dialog.FileName),
                    ".json",
                    StringComparison.OrdinalIgnoreCase);

                StatusMessage = "匯出 TT 表中...";
                await _gameService.ExportTranspositionTableAsync(dialog.FileName, asJson);
                StatusMessage = "TT 表匯出完成";
            }
            catch (Exception ex)
            {
                StatusMessage = $"TT 表匯出失敗：{ex.Message}";
            }
        });
        ImportTranspositionTableCommand = new RelayCommand(async _ =>
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "匯入 TT 表",
                    Filter = "Chinese Chess TT|*.cctt;*.json|Binary|*.cctt|JSON|*.json|All files|*.*",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog() != true) return;

                var asJson = string.Equals(
                    Path.GetExtension(dialog.FileName),
                    ".json",
                    StringComparison.OrdinalIgnoreCase);

                StatusMessage = "匯入 TT 表中...";
                await _gameService.ImportTranspositionTableAsync(dialog.FileName, asJson);
                StatusMessage = "TT 表匯入完成";
            }
            catch (Exception ex)
            {
                StatusMessage = $"TT 表匯入失敗：{ex.Message}";
            }
        });
        HintCommand = new RelayCommand(async _ =>
        {
            try
            {
                StatusMessage = "提示走法中...";
                var hint = await _gameService.GetHintAsync();

                if (hint.BestMove.IsNull)
                {
                    StatusMessage = "目前沒有可用提示";
                }
                else
                {
                    var turn = _gameService.CurrentBoard.Turn;
                    StatusMessage = $"提示完成：{hint.BestMove} | 分數：{FormatHintScore(hint.Score)}（{(turn == PieceColor.Red ? "紅方" : "黑方")}）";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"提示失敗：{ex.Message}";
            }
        });

        _gameService.SetDifficulty(_searchDepth, _searchThinkingTime * 1000);

        _gameService.GameMessage += msg => StatusMessage = msg;
    }

    private static string FormatHintScore(int score)
    {
        return score switch
        {
            > 0 => $"+{score}",
            < 0 => score.ToString(),
            _ => "0"
        };
    }
}
