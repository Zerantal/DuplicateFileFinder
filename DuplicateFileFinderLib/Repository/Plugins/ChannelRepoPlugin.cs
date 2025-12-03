using System.Threading.Channels;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinderLib.Repository.Plugins;

public abstract class ChannelRepoPlugin : IRepoPlugin
{
    private readonly Channel<RepoEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;

    private readonly TaskCompletionSource _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected ChannelRepoPlugin(int capacity = 1024)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        };

        _channel = Channel.CreateBounded<RepoEvent>(options);
        _workerTask = Task.Run(ProcessLoopAsync);
    }

    public void Post(RepoEvent evt)
    {
        // Must be non-blocking; drop if full.
        _channel.Writer.TryWrite(evt);
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await HandleEventAsync(evt, _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    OnEventProcessingError(ex, evt);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // TODO: graceful shutdown
        }
    }

    protected abstract ValueTask HandleEventAsync(RepoEvent evt, CancellationToken ct);

    protected virtual void OnEventProcessingError(Exception ex, RepoEvent evt)
    {
        Console.Error.WriteLine($"[{GetType().Name}] Error handling {evt.GetType().Name}: {ex}");
    }

    /// <summary>
    /// Call this from derived class once the bootstrap event is fully processed.
    /// </summary>
    protected void SignalReady()
        => _readyTcs.TrySetResult();

    public async Task WhenReadyAsync(CancellationToken ct = default)
    {
        if (!ct.CanBeCanceled)
        {
            await _readyTcs.Task.ConfigureAwait(false);
            return;
        }

        using var reg = ct.Register(() => _readyTcs.TrySetCanceled(ct));
        await _readyTcs.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        await _workerTask.ConfigureAwait(false);
    }
}
