using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ClipClip.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _canRun;

    public RelayCommand(Action run, Func<bool>? canRun = null)
        : this(_ => run(), canRun == null ? null : _ => canRun()) { }

    public RelayCommand(Action<object?> run, Func<object?, bool>? canRun = null)
    {
        _run = run;
        _canRun = canRun;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canRun?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _run(parameter);
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}
