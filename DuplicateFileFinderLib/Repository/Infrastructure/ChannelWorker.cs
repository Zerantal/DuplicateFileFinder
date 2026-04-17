using System.Threading.Channels;

namespace DuplicateFileFinderLib.Repository.Infrastructure;

public abstract class ChannelWorker<T> : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<T> _channel;
    private readonly Task _workerTask;

    protected ChannelWorker(int capacity, BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait)
    {
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = fullMode,
            SingleReader = true,
            SingleWriter = false
        });

        _workerTask = Task.Run(ProcessLoopAsync);
    }

    protected bool TryPost(T item) => _channel.Writer.TryWrite(item);

    protected ValueTask PostAsync(T item, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(item, ct);

    private async Task ProcessLoopAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await ProcessItemAsync(item, _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    OnItemProcessingError(ex, item);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    protected abstract ValueTask ProcessItemAsync(T item, CancellationToken ct);

    protected virtual void OnItemProcessingError(Exception ex, T item)
    {
    }

    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();
        await _workerTask.ConfigureAwait(false);

        await DisposeAsyncCore().ConfigureAwait(false);
        _cts.Dispose();
    }
}
