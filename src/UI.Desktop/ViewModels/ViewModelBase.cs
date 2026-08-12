using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ZBS.UI.Desktop.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Минимальная ICommand без внешних зависимостей — в духе zero-bloat.</summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

/// <summary>Команда с параметром из XAML (CommandParameter) — для меню оценок и т.п.</summary>
public sealed class ParamRelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>
/// Асинхронная команда: исключение не убегает в async void (краш процесса),
/// а уходит в onError; повторный клик во время работы игнорируется.
/// </summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Action<Exception>? onError = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        try { await execute(); }
        catch (Exception ex) { onError?.Invoke(ex); }
        finally { _running = false; }
    }
}
