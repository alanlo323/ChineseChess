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
    private bool _isHintFrom;
    private bool _isHintTo;
    private bool _isLastMove;
    
    public int Index { get; }
    public int Row { get; }
    public int Col { get; }
    
    // 棋盤座標定位（相對於棋盤尺寸，若採像素模式可在這裡處理）
    // UniformGrid 下順序很重要。
    
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

    public bool IsValidMove // 用於高亮可走點
    {
        get => _isValidMove;
        set => SetProperty(ref _isValidMove, value);
    }

    public bool IsHintFrom
    {
        get => _isHintFrom;
        set => SetProperty(ref _isHintFrom, value);
    }

    public bool IsHintTo
    {
        get => _isHintTo;
        set => SetProperty(ref _isHintTo, value);
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
    private int? _hintFrom;
    private int? _hintTo;
    private string? _hintBoardFen;

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
            // 棋盤 Index 範圍 0..89。
            // Row 0 為上方。
            Squares.Add(new SquareViewModel(i, i / 9, i % 9));
        }
    }

    private void OnBoardUpdated()
    {
        // 必須在 UI 執行緒執行
        System.Windows.Application.Current?.Dispatcher.Invoke(RefreshBoard);
    }

    private void OnHintReady(SearchResult hint)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _hintFrom = null;
            _hintTo = null;
            _hintBoardFen = null;
            ClearAllHighlights();

            if (hint.BestMove.IsNull) return;
            var from = hint.BestMove.From;
            var to = hint.BestMove.To;

            if (from < 90) _hintFrom = from;
            if (to < 90) _hintTo = to;
            _hintBoardFen = _gameService.CurrentBoard.ToFen();
            ApplyHintHighlights();
        });
    }

    private void RefreshBoard()
    {
        var board = _gameService.CurrentBoard;
        var currentFen = board.ToFen();

        ClearAllHighlights();

        for (int i = 0; i < 90; i++)
        {
            Squares[i].Piece = board.GetPiece(i);
            Squares[i].IsSelected = false;
            // TODO: 若歷史紀錄可用，會高亮上一手棋
        }

        if (_hintBoardFen != null && _hintBoardFen == currentFen)
        {
            ApplyHintHighlights();
        }
        else
        {
            _hintFrom = null;
            _hintTo = null;
            _hintBoardFen = null;
        }

        _selectedSquare = null;
    }

    private async void OnSquareClick(object? param)
    {
        if (param is not SquareViewModel square) return;
        if (_gameService.IsThinking) return;

        if (_selectedSquare == null)
        {
            // 選取目前行棋方的棋子
            if (!square.Piece.IsNone && square.Piece.Color == _gameService.CurrentBoard.Turn)
            {
                ClearMoveHighlights();
                _selectedSquare = square;
                square.IsSelected = true;
                HighlightLegalMoves(square.Index);
            }
        }
        else
        {
            // 移動或取消選取
            if (square == _selectedSquare)
            {
                _selectedSquare.IsSelected = false;
                _selectedSquare = null;
                ClearMoveHighlights();
                return;
            }

                // 嘗試移動
            if (square.Piece.Color == _gameService.CurrentBoard.Turn)
            {
                // 切換選取
                _selectedSquare.IsSelected = false;
                ClearMoveHighlights();
                
                _selectedSquare = square;
                square.IsSelected = true;
                HighlightLegalMoves(square.Index);
            }
            else
            {
                // 移動到空位或吃子
                var from = _selectedSquare.Index;
                var move = new Move(from, square.Index);
                // 交由 service/board 驗證，實際由 service 負責
                
                ClearMoveHighlights();
                _selectedSquare.IsSelected = false;
                _selectedSquare = null;

                await _gameService.HumanMoveAsync(move);
            }
        }
    }

    private void ClearMoveHighlights()
    {
        foreach (var s in Squares)
        {
            s.IsValidMove = false;
        }
    }

    private void ClearHintHighlights()
    {
        foreach (var s in Squares)
        {
            s.IsHintFrom = false;
            s.IsHintTo = false;
        }
    }

    private void ClearAllHighlights()
    {
        ClearMoveHighlights();
        ClearHintHighlights();
    }

    private void ApplyHintHighlights()
    {
        if (_hintFrom.HasValue && _hintFrom.Value < 90 && _hintFrom.Value >= 0)
        {
            Squares[_hintFrom.Value].IsHintFrom = true;
        }

        if (_hintTo.HasValue && _hintTo.Value < 90 && _hintTo.Value >= 0)
        {
            Squares[_hintTo.Value].IsHintTo = true;
        }
    }

    private void HighlightLegalMoves(int fromIndex)
    {
        ClearMoveHighlights();
        var moves = _gameService.CurrentBoard.GenerateLegalMoves().Where(m => m.From == fromIndex);
        foreach (var move in moves)
        {
            Squares[move.To].IsValidMove = true;
        }
    }
}
