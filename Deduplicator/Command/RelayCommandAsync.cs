#nullable enable
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using DuplicateFileFinder.Util;

namespace DuplicateFileFinder.Command;

//todo: implement error handling
// ReSharper disable once UnusedMember.Global
public class RelayCommandAsync : IAsyncCommand
{
    public event EventHandler? CanExecuteChanged;
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly IErrorHandler? _errorHandler;
    private bool _isExecuting;

    public RelayCommandAsync(Func<Task> execute, Func<bool>? canExecute = null, IErrorHandler? errorHandler = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _errorHandler = errorHandler;
    }


    public async Task ExecuteAsync()
    {
        if (CanExecute())
        {
            try
            {
                _isExecuting = true;
                await _execute();
            }
            finally
            {
                _isExecuting = false;
            }
        }

        RaiseCanExecuteChanged();

    }

    private void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CanExecute()
    {
        return !_isExecuting && (_canExecute?.Invoke() ?? true);
    }

    #region ICommand Implementations
    bool ICommand.CanExecute(object? parameter)
    {
        return CanExecute();
    }

    void ICommand.Execute(object? parameter)
    {
        ExecuteAsync().FireAndForgetSafeAsync(_errorHandler);
    }
    #endregion

}