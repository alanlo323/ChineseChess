using ChineseChess.Application.Interfaces;
using ChineseChess.Domain.Entities;
using ChineseChess.Domain.Enums;
using ChineseChess.WPF.Core;
using System;
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

    private Piece piece;
    private bool isSelected;
    private bool isValidMove;
    private bool isHintFrom;
    private bool isHintTo;
    private bool isLastMoveFrom;
    private bool isLastMoveTo;
    private bool hasSmartHint;
    private int smartHintScore;
    private bool hasGhostPiece;
    private Piece ghostPiece;
    private bool hasHintGhostPiece;
    private Piece hintGhostPiece;

    public int Index { get; }
    public int Row { get; }
    public int Col { get; }
    public double BoardX { get; }
    public double BoardY { get; }
    public double HitBoxLeft { get; }
    public double HitBoxTop { get; }

    public Piece Piece
    {
        get => piece;
        set => SetProperty(ref piece, value);
    }

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public bool IsValidMove // 用於高亮可走點
    {
        get => isValidMove;
        set => SetProperty(ref isValidMove, value);
    }

    public bool IsHintFrom
    {
        get => isHintFrom;
        set => SetProperty(ref isHintFrom, value);
    }

    public bool IsHintTo
    {
        get => isHintTo;
        set => SetProperty(ref isHintTo, value);
    }

    public bool IsLastMoveFrom
    {
        get => isLastMoveFrom;
        set => SetProperty(ref isLastMoveFrom, value);
    }

    public bool IsLastMoveTo
    {
        get => isLastMoveTo;
        set => SetProperty(ref isLastMoveTo, value);
    }

    /// <summary>是否顯示智能提示評分 Badge</summary>
    public bool HasSmartHint
    {
        get => hasSmartHint;
        set => SetProperty(ref hasSmartHint, value);
    }

    /// <summary>走法評分（從「做出走法的玩家」視角，正分=有利）</summary>
    public int SmartHintScore
    {
        get => smartHintScore;
        set
        {
            if (SetProperty(ref smartHintScore, value))
                OnPropertyChanged(nameof(SmartHintScoreText));
        }
    }

    /// <summary>格式化評分文字，供 UI 顯示</summary>
    public string SmartHintScoreText => smartHintScore switch
    {
        > 0 => $"+{smartHintScore}",
        < 0 => smartHintScore.ToString(),
        _ => "0"
    };

    /// <summary>是否顯示虛影棋子（最佳走法落點）</summary>
    public bool HasGhostPiece
    {
        get => hasGhostPiece;
        set => SetProperty(ref hasGhostPiece, value);
    }

    /// <summary>虛影棋子（顯示哪顆棋子的虛影）</summary>
    public Piece GhostPiece
    {
        get => ghostPiece;
        set => SetProperty(ref ghostPiece, value);
    }

    /// <summary>是否顯示普通提示虛影棋子（提示走法的落點）</summary>
    public bool HasHintGhostPiece
    {
        get => hasHintGhostPiece;
        set => SetProperty(ref hasHintGhostPiece, value);
    }

    /// <summary>普通提示虛影棋子</summary>
    public Piece HintGhostPiece
    {
        get => hintGhostPiece;
        set => SetProperty(ref hintGhostPiece, value);
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
        piece = Piece.None;
        ghostPiece = Piece.None;
        hintGhostPiece = Piece.None;
    }
}

public class ChessBoardViewModel : ObservableObject, IDisposable
{
    private readonly IGameService gameService;
    private SquareViewModel? selectedSquare;
    private int? hintFrom;
    private int? hintTo;
    private string? hintBoardFen;
    private int? lastMoveFrom;
    private int? lastMoveTo;

    public ObservableCollection<SquareViewModel> Squares { get; } = new ObservableCollection<SquareViewModel>();

    public ICommand SquareClickCommand { get; }

    public ChessBoardViewModel(IGameService gameService)
    {
        this.gameService = gameService;
        this.gameService.BoardUpdated += OnBoardUpdated;
        this.gameService.HintReady += OnHintReady;
        this.gameService.SmartHintReady += OnSmartHintReady;
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
            hintFrom = null;
            hintTo = null;
            hintBoardFen = null;
            ClearHintHighlights();

            if (hint.BestMove.IsNull) return;
            var from = hint.BestMove.From;
            var to = hint.BestMove.To;

            if (from < 90) hintFrom = from;
            if (to < 90) hintTo = to;
            hintBoardFen = gameService.CurrentBoard.ToFen();
            ApplyHintHighlights();

            // 在落點顯示虛影棋子（讓玩家清楚看到是哪顆棋子移動到哪裡）
            if (hintFrom.HasValue && hintTo.HasValue)
            {
                var movingPiece = gameService.CurrentBoard.GetPiece(hintFrom.Value);
                if (!movingPiece.IsNone)
                {
                    Squares[hintTo.Value].HintGhostPiece = movingPiece;
                    Squares[hintTo.Value].HasHintGhostPiece = true;
                }
            }
        });
    }

    private void RefreshBoard()
    {
        var board = gameService.CurrentBoard;
        var currentFen = board.ToFen();

        ClearAllHighlights();

        for (int i = 0; i < 90; i++)
        {
            Squares[i].Piece = board.GetPiece(i);
            Squares[i].IsSelected = false;
        }

        lastMoveFrom = null;
        lastMoveTo = null;
        if (gameService.LastMove is { } lastMove)
        {
            lastMoveFrom = lastMove.From;
            lastMoveTo = lastMove.To;
        }

        ApplyLastMoveHighlights();

        if (hintBoardFen != null && hintBoardFen == currentFen)
        {
            ApplyHintHighlights();
        }
        else
        {
            hintFrom = null;
            hintTo = null;
            hintBoardFen = null;
        }

        selectedSquare = null;
    }

    private async void OnSquareClick(object? param)
    {
        try
        {
            if (param is not SquareViewModel square) return;
            if (gameService.IsThinking) return;

            if (selectedSquare == null)
            {
                // 選取目前行棋方的棋子
                if (!square.Piece.IsNone && square.Piece.Color == gameService.CurrentBoard.Turn)
                {
                    ClearMoveHighlights();
                    ClearSmartHintHighlights();
                    selectedSquare = square;
                    square.IsSelected = true;
                    HighlightLegalMoves(square.Index);
                    await gameService.RequestSmartHintAsync(square.Index);
                }
            }
            else
            {
                // 移動或取消選取
                if (square == selectedSquare)
                {
                    selectedSquare.IsSelected = false;
                    selectedSquare = null;
                    ClearMoveHighlights();
                    ClearSmartHintHighlights();
                    return;
                }

                // 嘗試移動
                if (square.Piece.Color == gameService.CurrentBoard.Turn)
                {
                    // 切換選取
                    selectedSquare.IsSelected = false;
                    ClearMoveHighlights();
                    ClearSmartHintHighlights();

                    selectedSquare = square;
                    square.IsSelected = true;
                    HighlightLegalMoves(square.Index);
                    await gameService.RequestSmartHintAsync(square.Index);
                }
                else
                {
                    // 移動到空位或吃子
                    var from = selectedSquare.Index;
                    var move = new Move(from, square.Index);

                    ClearMoveHighlights();
                    ClearSmartHintHighlights();
                    selectedSquare.IsSelected = false;
                    selectedSquare = null;

                    await gameService.HumanMoveAsync(move);
                }
            }
        }
        catch (Exception ex)
        {
            // async void 中的未處理例外會導致應用程式崩潰，在此統一攔截並顯示訊息
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                System.Windows.MessageBox.Show($"操作時發生錯誤：{ex.Message}", "錯誤",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error));
        }
    }

    private void OnSmartHintReady(IReadOnlyList<MoveEvaluation> evaluations)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ClearSmartHintHighlights();

            if (selectedSquare == null) return;
            var selectedPiece = selectedSquare.Piece;

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
            s.HasHintGhostPiece = false;
            s.HintGhostPiece = Piece.None;
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
        if (lastMoveFrom.HasValue && lastMoveFrom.Value >= 0 && lastMoveFrom.Value < 90)
        {
            Squares[lastMoveFrom.Value].IsLastMoveFrom = true;
        }

        if (lastMoveTo.HasValue && lastMoveTo.Value >= 0 && lastMoveTo.Value < 90)
        {
            Squares[lastMoveTo.Value].IsLastMoveTo = true;
        }
    }

    private void ApplyHintHighlights()
    {
        if (hintFrom.HasValue && hintFrom.Value < 90 && hintFrom.Value >= 0)
        {
            Squares[hintFrom.Value].IsHintFrom = true;
        }

        if (hintTo.HasValue && hintTo.Value < 90 && hintTo.Value >= 0)
        {
            Squares[hintTo.Value].IsHintTo = true;
        }
    }

    private void HighlightLegalMoves(int fromIndex)
    {
        ClearMoveHighlights();
        var moves = gameService.CurrentBoard.GenerateLegalMoves().Where(m => m.From == fromIndex);
        foreach (var move in moves)
        {
            Squares[move.To].IsValidMove = true;
        }
    }

    public void Dispose()
    {
        // 取消訂閱 GameService 事件，防止 ViewModel 因事件持有而無法被 GC
        gameService.BoardUpdated -= OnBoardUpdated;
        gameService.HintReady -= OnHintReady;
        gameService.SmartHintReady -= OnSmartHintReady;
    }
}
