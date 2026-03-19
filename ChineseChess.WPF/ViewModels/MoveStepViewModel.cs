using ChineseChess.WPF.Core;

namespace ChineseChess.WPF.ViewModels;

/// <summary>棋譜列表中的單步顯示 VM。</summary>
public class MoveStepViewModel : ObservableObject
{
    private bool isCurrent;

    public int StepNumber { get; init; }
    public string Notation { get; init; } = string.Empty;
    public string TurnLabel { get; init; } = string.Empty;

    /// <summary>是否為目前重播定格的步驟（高亮顯示）。</summary>
    public bool IsCurrent
    {
        get => isCurrent;
        set => SetProperty(ref isCurrent, value);
    }

    /// <summary>顯示文字，如「1. 紅  炮二平五」。</summary>
    public string DisplayText => $"{StepNumber}. {TurnLabel}  {Notation}";
}
