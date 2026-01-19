using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.WPF.Core;
using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

public class ControlPanelViewModel : ObservableObject
{
    private readonly IGameService _gameService;
    private GameMode _selectedMode = GameMode.PlayerVsAi;
    private string _statusMessage = "Ready";
    private int _searchDepth = 5;

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
                _gameService.SetDifficulty(value, 3000);
            }
        }
    }

    public ICommand StartGameCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand HintCommand { get; }

    public ControlPanelViewModel(IGameService gameService)
    {
        _gameService = gameService;
        StartGameCommand = new RelayCommand(async _ => await _gameService.StartGameAsync(SelectedMode));
        UndoCommand = new RelayCommand(_ => _gameService.Undo());
        HintCommand = new RelayCommand(async _ => await _gameService.GetHintAsync()); 

        _gameService.GameMessage += msg => StatusMessage = msg;
    }
}
