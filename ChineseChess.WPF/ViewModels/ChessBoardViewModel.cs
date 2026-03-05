using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.WPF.Core;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

public class SquareViewModel : ObservableObject
{
    private const double ColumnSpacing = 60.0;
    private const double RowSpacing = 56.0;
    private const double RiverGap = 56.0;
    private const double HitBoxHalfWidth = 30.0;
    private const double HitBoxHalfHeight = 28.0;

    private Piece _piece;
    private bool _isSelected;
    private bool _isValidMove;
    private bool _isHintFrom;
    private bool _isHintTo;
    private bool _isLastMoveFrom;
    private bool _isLastMoveTo;
    private bool _hasSmartHint;
    private int _smartHintScore;
    private bool _hasGhostPiece;
    private Piece _ghostPiece;

    public int Index { get; }
    public int Row { get; }
    public int Col { get; }
    public double BoardX { get; }
    public double BoardY { get; }
    public double HitBoxLeft { get; }
    public double HitBoxTop { get; }

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

    public bool IsLastMoveFrom
    {
        get => _isLastMoveFrom;
        set => SetProperty(ref _isLastMoveFrom, value);
    }

    public bool IsLastMoveTo
    {
        get => _isLastMoveTo;
        set => SetProperty(ref _isLastMoveTo, value);
    }

    /// <summary>是否顯示智能提示評分 Badge</summary>
    public bool HasSmartHint
    {
        get => _hasSmartHint;
        set => SetProperty(ref _hasSmartHint, value);
    }

    /// <summary>走法評分（從「做出走法的玩家」視角，正分=有利）</summary>
    public int SmartHintScore
    {
        get => _smartHintScore;
        set
        {
            if (SetProperty(ref _smartHintScore, value))
                OnPropertyChanged(nameof(SmartHintScoreText));
        }
    }

    /// <summary>格式化評分文字，供 UI 顯示</summary>
    public string SmartHintScoreText => _smartHintScore switch
    {
        > 0 => $"+{_smartHintScore}",
        < 0 => _smartHintScore.ToString(),
        _ => "0"
    };

    /// <summary>是否顯示虛影棋子（最佳走法落點）</summary>
    public bool HasGhostPiece
    {
        get => _hasGhostPiece;
        set => SetProperty(ref _hasGhostPiece, value);
    }

    /// <summary>虛影棋子（顯示哪顆棋子的虛影）</summary>
    public Piece GhostPiece
    {
        get => _ghostPiece;
        set => SetProperty(ref _ghostPiece, value);
    }

    public SquareViewModel(int index, int row, int col)
    {
        Index = index;
        Row = row;
        Col = col;
        BoardX = col * ColumnSpacing;
        BoardY = row * RowSpacing + (row >= 5 ? RiverGap : 0.0);
        HitBoxLeft = BoardX - HitBoxHalfWidth;
        HitBoxTop = BoardY - HitBoxHalfHeight;
        _piece = Piece.None;
        _ghostPiece = Piece.None;
    }
}

public class ChessBoardViewModel : ObservableObject
{
    private readonly IGameService _gameService;
    private SquareViewModel? _selectedSquare;
    private int? _hintFrom;
    private int? _hintTo;
    private string? _hintBoardFen;
    private int? _lastMoveFrom;
    private int? _lastMoveTo;

    public ObservableCollection<SquareViewModel> Squares { get; } = new ObservableCollection<SquareViewModel>();

    public ICommand SquareClickCommand { get; }

    public ChessBoardViewModel(IGameService gameService)
    {
        _gameService = gameService;
        _gameService.BoardUpdated += OnBoardUpdated;
        _gameService.HintReady += OnHintReady;
        _gameService.SmartHintReady += OnSmartHintReady;
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
            ClearHintHighlights();

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
        }

        _lastMoveFrom = null;
        _lastMoveTo = null;
        if (_gameService.LastMove is { } lastMove)
        {
            _lastMoveFrom = lastMove.From;
            _lastMoveTo = lastMove.To;
        }

        ApplyLastMoveHighlights();

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
                ClearSmartHintHighlights();
                _selectedSquare = square;
                square.IsSelected = true;
                HighlightLegalMoves(square.Index);
                await _gameService.RequestSmartHintAsync(square.Index);
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
                ClearSmartHintHighlights();
                return;
            }

            // 嘗試移動
            if (square.Piece.Color == _gameService.CurrentBoard.Turn)
            {
                // 切換選取
                _selectedSquare.IsSelected = false;
                ClearMoveHighlights();
                ClearSmartHintHighlights();

                _selectedSquare = square;
                square.IsSelected = true;
                HighlightLegalMoves(square.Index);
                await _gameService.RequestSmartHintAsync(square.Index);
            }
            else
            {
                // 移動到空位或吃子
                var from = _selectedSquare.Index;
                var move = new Move(from, square.Index);

                ClearMoveHighlights();
                ClearSmartHintHighlights();
                _selectedSquare.IsSelected = false;
                _selectedSquare = null;

                await _gameService.HumanMoveAsync(move);
            }
        }
    }

    private void OnSmartHintReady(IReadOnlyList<MoveEvaluation> evaluations)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ClearSmartHintHighlights();

            if (_selectedSquare == null) return;
            var selectedPiece = _selectedSquare.Piece;

            foreach (var eval in evaluations)
            {
                var toIndex = eval.Move.To;
                if (toIndex < 0 || toIndex >= 90) continue;

                var sq = Squares[toIndex];
                sq.HasSmartHint = true;
                sq.SmartHintScore = eval.Score;

                if (eval.IsBest)
                {
                    sq.HasGhostPiece = true;
                    sq.GhostPiece = selectedPiece;
                }
            }
        });
    }

    private void ClearMoveHighlights()
    {
        foreach (var s in Squares)
        {
            s.IsValidMove = false;
        }
    }

    private void ClearSmartHintHighlights()
    {
        foreach (var s in Squares)
        {
            s.HasSmartHint = false;
            s.SmartHintScore = 0;
            s.HasGhostPiece = false;
            s.GhostPiece = Piece.None;
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

    private void ClearLastMoveHighlights()
    {
        foreach (var s in Squares)
        {
            s.IsLastMoveFrom = false;
            s.IsLastMoveTo = false;
        }
    }

    private void ClearAllHighlights()
    {
        ClearMoveHighlights();
        ClearHintHighlights();
        ClearLastMoveHighlights();
        ClearSmartHintHighlights();
    }

    private void ApplyLastMoveHighlights()
    {
        if (_lastMoveFrom.HasValue && _lastMoveFrom.Value >= 0 && _lastMoveFrom.Value < 90)
        {
            Squares[_lastMoveFrom.Value].IsLastMoveFrom = true;
        }

        if (_lastMoveTo.HasValue && _lastMoveTo.Value >= 0 && _lastMoveTo.Value < 90)
        {
            Squares[_lastMoveTo.Value].IsLastMoveTo = true;
        }
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
