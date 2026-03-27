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
/// 擺棋模式用 ViewModel：以視覺調色板選擇棋子、切換橡皮擦模式、設定行棋方、清空/重置/確認局面。
/// </summary>
public class BoardSetupViewModel : ObservableObject
{
    private readonly ICoreGameService gameService;
    private Piece selectedPiece;
    private bool isEraseMode;
    private PieceColor setupTurn = PieceColor.Red;
    private string validationMessage = string.Empty;
    private bool isValid;
    private GameMode targetMode = GameMode.PlayerVsAi;

    /// <summary>目前選取要放置的棋子（直接設定；選棋時自動關閉橡皮擦模式）。</summary>
    public Piece SelectedPiece
    {
        get => selectedPiece;
        set
        {
            if (SetProperty(ref selectedPiece, value))
                IsEraseMode = false;
        }
    }

    /// <summary>橡皮擦模式：啟用時左鍵點擊棋盤移除棋子，而非放置。</summary>
    public bool IsEraseMode
    {
        get => isEraseMode;
        set => SetProperty(ref isEraseMode, value);
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

    /// <summary>目標模式（確認時使用），預設 PlayerVsAi。</summary>
    public GameMode TargetMode
    {
        get => targetMode;
        set => SetProperty(ref targetMode, value);
    }

    /// <summary>14 顆棋子調色板（紅方 7 種在前，黑方 7 種在後），供 ListBox 綁定。</summary>
    public IReadOnlyList<Piece> PiecePalette { get; } = new[]
    {
        new Piece(PieceColor.Red, PieceType.King),
        new Piece(PieceColor.Red, PieceType.Advisor),
        new Piece(PieceColor.Red, PieceType.Elephant),
        new Piece(PieceColor.Red, PieceType.Horse),
        new Piece(PieceColor.Red, PieceType.Rook),
        new Piece(PieceColor.Red, PieceType.Cannon),
        new Piece(PieceColor.Red, PieceType.Pawn),
        new Piece(PieceColor.Black, PieceType.King),
        new Piece(PieceColor.Black, PieceType.Advisor),
        new Piece(PieceColor.Black, PieceType.Elephant),
        new Piece(PieceColor.Black, PieceType.Horse),
        new Piece(PieceColor.Black, PieceType.Rook),
        new Piece(PieceColor.Black, PieceType.Cannon),
        new Piece(PieceColor.Black, PieceType.Pawn),
    };

    public ICommand ClearBoardCommand { get; }
    public ICommand ResetBoardCommand { get; }
    public ICommand ConfirmCommand { get; }

    /// <summary>確認成功後觸發，傳入目標 GameMode 讓外層決定如何啟動遊戲。</summary>
    public event Action<GameMode>? SetupConfirmed;

    public BoardSetupViewModel(ICoreGameService gameService)
    {
        this.gameService = gameService;
        selectedPiece = PiecePalette[0];

        ClearBoardCommand = new RelayCommand(_ => { gameService.SetupClearBoard(); ValidationMessage = string.Empty; });
        ResetBoardCommand = new RelayCommand(_ => { gameService.SetupResetBoard(); ValidationMessage = string.Empty; });
        ConfirmCommand = new AsyncRelayCommand(async _ => await ConfirmAsync());
    }

    private async Task ConfirmAsync()
    {
        try
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
        catch (InvalidOperationException ex)
        {
            IsValid = false;
            ValidationMessage = $"確認局面時發生錯誤：{ex.Message}";
        }
    }
}
