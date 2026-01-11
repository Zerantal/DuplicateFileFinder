using System.Reactive.Concurrency;
using System.Windows.Input;

using ReactiveUI;

namespace DuplicateFileFinder.Gui.Infrastructure.Util;

public static class Commands
{
    public static readonly ICommand DisabledCommand =
        ReactiveCommand.Create(() => { }, outputScheduler: ImmediateScheduler.Instance);
}
