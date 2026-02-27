using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.WPF.Core;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

public class SquareViewModel : ObservableObject
{
    private Piece _piece;
    private bool _isSelected;
    private bool _isValidMove;
    private bool _isLastMove;
    
    public int Index { get; }
    public int Row { get; }
    public int Col { get; }
    
    // Canvas Positioning (Relative to board size, handled in View or here if pixel based)
    // For UniformGrid, order matters.
    
    public Piece Piece
    {
        get => _piece;
        set => SetProperty(ref _piece, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsValidMove // For highlighting dots
    {
        get => _isValidMove;
        set => SetProperty(ref _isValidMove, value);
    }

    public bool IsLastMove
    {
        get => _isLastMove;
        set => SetProperty(ref _isLastMove, value);
    }

    public SquareViewModel(int index, int row, int col)
    {
        Index = index;
        Row = row;
        Col = col;
        _piece = Piece.None;
    }
}

public class ChessBoardViewModel : ObservableObject
{
    private readonly IGameService _gameService;
    private SquareViewModel? _selectedSquare;

    public ObservableCollection<SquareViewModel> Squares { get; } = new ObservableCollection<SquareViewModel>();
    
    public ICommand SquareClickCommand { get; }

    public ChessBoardViewModel(IGameService gameService)
    {
        _gameService = gameService;
        _gameService.BoardUpdated += OnBoardUpdated;
        _gameService.HintReady += OnHintReady;
        SquareClickCommand = new RelayCommand(OnSquareClick);

        InitializeBoard();
        RefreshBoard();
    }

    private void InitializeBoard()
    {
        for (int i = 0; i < 90; i++)
        {
            // Board Index 0..89. 
            // Row 0 is Top.
            Squares.Add(new SquareViewModel(i, i / 9, i % 9));
        }
    }

    private void OnBoardUpdated()
    {
        // Must run on UI thread
        System.Windows.Application.Current?.Dispatcher.Invoke(RefreshBoard);
    }

    private void OnHintReady(SearchResult hint)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ClearHighlights();
            if (hint.BestMove.IsNull) return;
            var from = hint.BestMove.From;
            var to = hint.BestMove.To;

            if (from < 90) Squares[from].IsValidMove = true;
            if (to < 90) Squares[to].IsValidMove = true;
        });
    }

    private void RefreshBoard()
    {
        var board = _gameService.CurrentBoard;
        for (int i = 0; i < 90; i++)
        {
            Squares[i].Piece = board.GetPiece(i);
            Squares[i].IsSelected = false;
            Squares[i].IsValidMove = false;
            // TODO: Highlight last move from history if available
        }
        _selectedSquare = null;
    }

    private async void OnSquareClick(object? param)
    {
        if (param is not SquareViewModel square) return;
        if (_gameService.IsThinking) return;

        if (_selectedSquare == null)
        {
            // Select piece of current turn
            if (!square.Piece.IsNone && square.Piece.Color == _gameService.CurrentBoard.Turn)
            {
                _selectedSquare = square;
                square.IsSelected = true;
                HighlightLegalMoves(square.Index);
            }
        }
        else
        {
            // Move or Deselect
            if (square == _selectedSquare)
            {
                _selectedSquare.IsSelected = false;
                _selectedSquare = null;
                ClearHighlights();
                return;
            }

            // Attempt Move
            if (square.Piece.Color == _gameService.CurrentBoard.Turn)
            {
                // Switch selection
                _selectedSquare.IsSelected = false;
                ClearHighlights();
                
                _selectedSquare = square;
                square.IsSelected = true;
                HighlightLegalMoves(square.Index);
            }
            else
            {
                // Move to empty or capture
                var from = _selectedSquare.Index;
                var move = new Move(from, square.Index);
                // Validate via service/board? Service handles it.
                
                ClearHighlights();
                _selectedSquare.IsSelected = false;
                _selectedSquare = null;

                await _gameService.HumanMoveAsync(move);
            }
        }
    }

    private void ClearHighlights()
    {
        foreach (var s in Squares) s.IsValidMove = false;
    }

    private void HighlightLegalMoves(int fromIndex)
    {
        ClearHighlights();
        var moves = _gameService.CurrentBoard.GenerateLegalMoves().Where(m => m.From == fromIndex);
        foreach (var move in moves)
        {
            Squares[move.To].IsValidMove = true;
        }
    }
}
