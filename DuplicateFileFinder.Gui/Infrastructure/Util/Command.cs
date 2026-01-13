using System.Windows.Input;

namespace DuplicateFileFinder.Gui.Infrastructure.Util;

public static class Commands
{
    public static readonly ICommand DisabledCommand = new DisabledCommandImpl();

    private sealed class DisabledCommandImpl : ICommand
    {
        public bool CanExecute(object? parameter) => false;

        public void Execute(object? parameter)
        {
            // Intentionally a no-op.
        }

        // ReSharper disable once EventNeverSubscribedTo.Local
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
