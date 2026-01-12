using NLog;

namespace DuplicateFileFinder.Gui.Infrastructure.Util;

public sealed class DisposableManager : IDisposable
{
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private readonly Lock _gate = new();
    private Stack<Action>? _cleanupActions = new();
    private bool _isDisposed;

    /// <summary>Tracks an IDisposable object to be disposed of later.</summary>
    public T Track<T>(T disposable) where T : IDisposable
    {
        if (disposable is null) throw new ArgumentNullException(nameof(disposable));
        Add(disposable.Dispose);
        return disposable;
    }

    /// <summary>Adds a custom cleanup action (like unregistering an event).</summary>
    public void Add(Action cleanupAction)
    {
        if (cleanupAction is null) throw new ArgumentNullException(nameof(cleanupAction));

        Action? runNow = null;

        lock (_gate)
        {
            if (_isDisposed)
                runNow = cleanupAction;
            else
                _cleanupActions!.Push(cleanupAction);
        }

        // Execute outside the lock
        if (runNow is not null)
        {
            try { runNow(); }
            catch (Exception ex)
            {
                s_log.Error(ex, "Disposal error (late add).");
            }
        }
    }

    public void Dispose()
    {
        Stack<Action>? actions;

        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            actions = _cleanupActions;
            _cleanupActions = null;
        }

        if (actions is null) return;

        while (actions.Count > 0)
        {
            var action = actions.Pop();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                s_log.Error(ex, "Disposal error.");
            }
        }
    }
}
