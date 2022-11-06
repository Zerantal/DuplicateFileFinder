using System.Threading.Tasks;
using System.Windows.Input;

// ReSharper disable UnusedMemberInSuper.Global

namespace Deduplicator.Command;

public interface IAsyncCommand : ICommand
{
    Task ExecuteAsync();
    bool CanExecute();
}