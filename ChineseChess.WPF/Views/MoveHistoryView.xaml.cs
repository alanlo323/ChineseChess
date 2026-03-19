using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ChineseChess.WPF.ViewModels;

namespace ChineseChess.WPF.Views;

public partial class MoveHistoryView : UserControl
{
    public MoveHistoryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MoveHistoryViewModel old)
            old.ScrollToCurrent -= ScrollToCurrentStep;

        if (e.NewValue is MoveHistoryViewModel vm)
            vm.ScrollToCurrent += ScrollToCurrentStep;
    }

    private void ScrollToCurrentStep()
    {
        var current = moveListBox.Items
            .OfType<MoveStepViewModel>()
            .FirstOrDefault(s => s.IsCurrent);

        if (current != null)
            moveListBox.ScrollIntoView(current);
        else if (moveListBox.Items.Count > 0)
            moveListBox.ScrollIntoView(moveListBox.Items[^1]);
    }
}
