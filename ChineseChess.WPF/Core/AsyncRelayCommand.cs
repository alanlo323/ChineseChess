using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ChineseChess.WPF.Core;

/// <summary>
/// 支援 async/await 的 ICommand 實作。
/// 防止重複執行（isExecuting），並在 execute 拋出例外時不靜默吞掉。
/// </summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly Predicate<object?>? canExecute;
    private bool isExecuting;

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }

    public bool CanExecute(object? parameter) => !isExecuting && (canExecute == null || canExecute(parameter));

    public async void Execute(object? parameter)
    {
        if (isExecuting) return;
        isExecuting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute(parameter);
        }
        finally
        {
            isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
