using GitWizard;

namespace GitWizardUI.ViewModels;

/// <summary>Command interface for cross-platform use.</summary>
public interface ICommand
{
    event EventHandler? CanExecuteChanged;
    bool CanExecute(object? parameter);
    void Execute(object? parameter);
}

/// <summary>A command implementation for cross-platform use.</summary>
public sealed class RelayCommand : ICommand
{
    readonly Action _execute;
    readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A parameterized command implementation for cross-platform use.</summary>
public sealed class RelayCommand<T> : ICommand
{
    readonly Action<T> _execute;
    readonly Func<T, bool>? _canExecute;

    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute is null || (parameter is T t && _canExecute(t));

    public void Execute(object? parameter)
    {
        if (parameter is T t)
            _execute(t);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// An async command. <see cref="Execute"/> launches the task fire-and-forget but funnels any
/// failure through a single audited handler, so a faulted task can't crash the process the way a
/// raw <c>async void</c> lambda would.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    readonly Func<Task> _execute;
    readonly Func<bool>? _canExecute;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _ = ExecuteAsync();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    async Task ExecuteAsync()
    {
        try
        {
            await _execute();
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Async command failed.");
        }
    }
}

/// <summary>A parameterized async command. See <see cref="AsyncRelayCommand"/>.</summary>
public sealed class AsyncRelayCommand<T> : ICommand
{
    readonly Func<T, Task> _execute;
    readonly Func<T, bool>? _canExecute;

    public AsyncRelayCommand(Func<T, Task> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute is null || (parameter is T t && _canExecute(t));

    public void Execute(object? parameter)
    {
        if (parameter is T t)
            _ = ExecuteAsync(t);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    async Task ExecuteAsync(T parameter)
    {
        try
        {
            await _execute(parameter);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, "Async command failed.");
        }
    }
}
