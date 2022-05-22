using System.Threading.Tasks;
using System.Windows.Input;
// ReSharper disable UnusedMemberInSuper.Global

namespace DuplicateFileFinder.Command;

public interface IAsyncCommand : ICommand
{
    Task ExecuteAsync();
    bool CanExecute();
}