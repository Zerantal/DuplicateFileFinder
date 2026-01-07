using Avalonia.Threading;

namespace DuplicateFileFinder.Gui.Infrastructure.Toasts;

public sealed class ToastService(ToastHostViewModel host, TimeSpan? defaultDuration = null, int maxVisible = 4)
    : IToastService
{
    private readonly ToastHostViewModel _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly TimeSpan _defaultDuration = defaultDuration ?? TimeSpan.FromSeconds(3);
    private readonly int _maxVisible = Math.Max(1, maxVisible);

    public void Show(string message, ToastKind kind = ToastKind.Info, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var toast = new ToastItemViewModel(message, kind);
        var ttl = duration ?? _defaultDuration;

        Dispatcher.UIThread.Post(() =>
        {
            // cap count: drop oldest first
            while (_host.Items.Count >= _maxVisible)
                _host.Items.RemoveAt(0);

            _host.Add(toast);
        }, DispatcherPriority.Background);

        _ = ExpireAsync(toast, ttl);
    }

    private async Task ExpireAsync(ToastItemViewModel toast, TimeSpan ttl)
    {
        try
        {
            await Task.Delay(ttl).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        Dispatcher.UIThread.Post(() => _host.Remove(toast), DispatcherPriority.Background);
    }
}
