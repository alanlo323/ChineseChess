using ChineseChess.Domain.Enums;
using ChineseChess.WPF.Core;
using System;
using System.Windows.Input;

namespace ChineseChess.WPF.ViewModels;

/// <summary>
/// 單一棋子類型的數量選擇項目（供 PieceCountSelectorViewModel 使用）。
/// </summary>
public sealed class PieceCountItem : ObservableObject
{
    private int count;
    private readonly Action onCountChanged;

    public PieceCountItem(PieceType type, string label, int max, Action onCountChanged)
    {
        Type  = type;
        Label = label;
        Max   = max;
        this.onCountChanged = onCountChanged;

        IncrementCommand = new RelayCommand(_ => Count++, _ => count < Max);
        DecrementCommand = new RelayCommand(_ => Count--, _ => count > 0);
    }

    public PieceType Type  { get; }
    public string    Label { get; }
    public int       Max   { get; }

    public int Count
    {
        get => count;
        set
        {
            var clamped = Math.Clamp(value, 0, Max);
            if (SetProperty(ref count, clamped))
            {
                onCountChanged();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public ICommand IncrementCommand { get; }
    public ICommand DecrementCommand { get; }
}
