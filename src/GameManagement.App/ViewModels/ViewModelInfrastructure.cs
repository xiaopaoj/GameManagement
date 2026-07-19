using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace GameManagement.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => canExecute?.Invoke(Convert(parameter)) ?? true;
    public void Execute(object? parameter) => execute(Convert(parameter));
    private static T? Convert(object? parameter) => parameter is T value ? value : default;
}

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running; public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter) { if (!CanExecute(parameter)) return; _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty); try { await execute(); } finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); } }
}

public sealed class AsyncRelayCommand<T>(Func<T?, Task> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(Convert(parameter)) ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(Convert(parameter)); }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
    private static T? Convert(object? parameter) => parameter is T value ? value : default;
}
