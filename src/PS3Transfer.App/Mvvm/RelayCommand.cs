using System.Windows.Input;

namespace PS3Transfer.App.Mvvm;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _can;

    public RelayCommand(Action execute, Func<bool>? can = null)
    {
        _execute = execute;
        _can = can;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _can?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _can;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? can = null)
    {
        _execute = execute;
        _can = can;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _can?.Invoke(Cast(parameter)) ?? true;

    public void Execute(object? parameter) => _execute(Cast(parameter));

    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? Cast(object? parameter) => parameter is T value ? value : default;
}
