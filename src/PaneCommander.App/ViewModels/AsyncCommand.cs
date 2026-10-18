using System.Windows.Input;
using PaneCommander.Core;

namespace PaneCommander.App.ViewModels;

public sealed class AsyncCommand(Func<object?, Task> execute, IErrorLogger logger, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try { await execute(parameter); }
        catch (Exception ex)
        {
            logger.Log("UIコマンド", ex);
            System.Windows.MessageBox.Show(ex.Message, "操作できません", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
