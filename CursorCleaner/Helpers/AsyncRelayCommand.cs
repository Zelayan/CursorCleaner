using System.Windows.Input;

namespace CursorCleaner.Helpers;

public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<object?, CancellationToken, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private CancellationTokenSource? _cancellation;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this((_, _) => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public AsyncRelayCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null)
        : this((_, token) => execute(token), canExecute is null ? null : _ => canExecute())
    {
    }

    public AsyncRelayCommand(
        Func<object?, CancellationToken, Task> execute,
        Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                NotifyCanExecuteChanged();
            }
        }
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter).ConfigureAwait(true);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        try
        {
            await _execute(parameter, _cancellation.Token).ConfigureAwait(true);
        }
        finally
        {
            IsRunning = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    public void Cancel() => _cancellation?.Cancel();
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
