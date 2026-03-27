using ChineseChess.Application.Enums;
using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.WPF.Core;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// 擺棋模式用 ViewModel：選擇要放置的棋子、設定行棋方、清空/重置/確認局面。
/// </summary>
public class BoardSetupViewModel : ObservableObject
{
    private readonly ICoreGameService gameService;
    private PieceColor selectedColor = PieceColor.Red;
    private PieceType selectedType = PieceType.King;
    private PieceColor setupTurn = PieceColor.Red;
    private string validationMessage = string.Empty;
    private bool isValid;

    public PieceColor SelectedColor
    {
        get => selectedColor;
        set => SetProperty(ref selectedColor, value);
    }

    public PieceType SelectedType
    {
        get => selectedType;
        set => SetProperty(ref selectedType, value);
    }

    public PieceColor SetupTurn
    {
        get => setupTurn;
        set
        {
            if (SetProperty(ref setupTurn, value))
                gameService.SetupSetTurn(value);
        }
    }

    public string ValidationMessage
    {
        get => validationMessage;
        private set => SetProperty(ref validationMessage, value);
    }

    public bool IsValid
    {
        get => isValid;
        private set => SetProperty(ref isValid, value);
    }

    /// <summary>目前選取要放置的棋子（由 SelectedColor + SelectedType 組成）。</summary>
    public Piece SelectedPiece => new Piece(SelectedColor, SelectedType);

    public IReadOnlyList<PieceColor> Colors { get; } = new[] { PieceColor.Red, PieceColor.Black };
    public IReadOnlyList<PieceType> PieceTypes { get; } = new[]
    {
        PieceType.King, PieceType.Advisor, PieceType.Elephant,
        PieceType.Horse, PieceType.Rook, PieceType.Cannon, PieceType.Pawn
    };
    public IReadOnlyList<PieceColor> TurnOptions { get; } = new[] { PieceColor.Red, PieceColor.Black };

    public ICommand ClearBoardCommand { get; }
    public ICommand ResetBoardCommand { get; }
    public ICommand ConfirmCommand { get; }

    /// <summary>確認成功後觸發，傳入目標 GameMode 讓外層決定如何啟動遊戲。</summary>
    public event Action<GameMode>? SetupConfirmed;

    /// <summary>目標模式（確認時使用）。</summary>
    public GameMode TargetMode { get; set; } = GameMode.PlayerVsPlayer;

    public BoardSetupViewModel(ICoreGameService gameService)
    {
        this.gameService = gameService;

        ClearBoardCommand = new RelayCommand(_ => gameService.SetupClearBoard());
        ResetBoardCommand = new RelayCommand(_ => gameService.SetupResetBoard());
        ConfirmCommand = new AsyncRelayCommand(async _ => await ConfirmAsync());
    }

    private async Task ConfirmAsync()
    {
        var result = await gameService.ConfirmSetupAsync(TargetMode);

        if (result.IsValid)
        {
            IsValid = true;
            ValidationMessage = "局面確認成功！";
            SetupConfirmed?.Invoke(TargetMode);
        }
        else
        {
            IsValid = false;
            ValidationMessage = string.Join(Environment.NewLine, result.Errors);
        }
    }

    /// <summary>取得棋子的顯示名稱（用於 UI 清單）。</summary>
    public static string GetPieceTypeName(PieceType type) => type switch
    {
        PieceType.King => "帥/將",
        PieceType.Advisor => "仕/士",
        PieceType.Elephant => "相/象",
        PieceType.Horse => "馬",
        PieceType.Rook => "車",
        PieceType.Cannon => "炮",
        PieceType.Pawn => "兵/卒",
        _ => type.ToString()
    };

    public static string GetColorName(PieceColor color) => color switch
    {
        PieceColor.Red => "紅方",
        PieceColor.Black => "黑方",
        _ => color.ToString()
    };
}
