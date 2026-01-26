// DuplicateFileFinder.Gui/Infrastructure/Services/AvaloniaUiDispatcher.cs

using Avalonia.Threading;

namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
        => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
}

