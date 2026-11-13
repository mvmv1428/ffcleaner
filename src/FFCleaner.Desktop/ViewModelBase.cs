using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace FFCleaner.Desktop;

public enum NoticeKind { Exito, Aviso, Info }

/// <summary>Aviso emergente que la ventana muestra unos segundos al terminar una operación.</summary>
public sealed record Notice(string Title, string Message, NoticeKind Kind);

public abstract class ObservableObject : INotifyPropertyChanged
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

/// <summary>Comando síncrono o asíncrono. Las excepciones no controladas llegan al manejador global de la app.</summary>
public sealed class RelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(() => { execute(); return Task.CompletedTask; }, canExecute) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter)
    {
        if (CanExecute(parameter)) await execute();
    }
}
