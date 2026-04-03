// DuplicateFileFinderLib/Repository/Plugins/ChannelRepoPlugin.cs

using System.Threading.Channels;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinderLib.Repository.Plugins;

public abstract class ChannelRepoPlugin : IRepoPlugin, IReadyState, IIndexGenerationBarrier
{
    private readonly Channel<RepoEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;

    private readonly TaskCompletionSource _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Lock _processedSync = new();
    private long _lastProcessedGeneration;
    private readonly List<GenerationWaiter> _generationWaiters = new();

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
            // shutdown
        }
    }

    protected virtual async ValueTask HandleEventAsync(RepoEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case BootstrapEvent bootstrap:
                using (TimingLog.Start($"Processing BootstrapEvent ({GetType().Name})"))
                {
                    await OnBootstrapEventAsync(bootstrap, ct).ConfigureAwait(false);
                }
                SignalReady();
                break;

            case ScanRunFinalisedEvent finalised:
                await OnScanRunFinalisedEventAsync(finalised, ct).ConfigureAwait(false);
                break;

            case ScanRootSnapshotReplacedEvent replaced:
                using (TimingLog.Start($"Processing ScanRootSnapshotReplacedEvent ({GetType().Name})"))
                {
                    await OnScanRootSnapshotReplacedEventAsync(replaced, ct).ConfigureAwait(false);
                }
                break;

            case RepoFileDeletedEvent fileDeleted:
                await OnRepoFileDeletedEventAsync(fileDeleted, ct).ConfigureAwait(false);
                break;

            case RepoDirDeletedEvent dirDeleted:
                await OnRepoDirDeletedEventAsync(dirDeleted, ct).ConfigureAwait(false);
                break;

            case RepoScanRootRemovedEvent rootRemoved:
                await OnRepoScanRootRemovedEventAsync(rootRemoved, ct).ConfigureAwait(false);
                break;

            case ScanRootMetaChangedEvent metaChanged:
                await OnScanRootMetaChangedEventAsync(metaChanged, ct).ConfigureAwait(false);
                break;
        }

        UpdateLastProcessedGeneration(evt.Generation);
    }

    // New async overridables (default no-op)
    protected virtual ValueTask OnScanRootSnapshotReplacedEventAsync(ScanRootSnapshotReplacedEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
    protected virtual ValueTask OnRepoFileDeletedEventAsync(RepoFileDeletedEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
    protected virtual ValueTask OnRepoDirDeletedEventAsync(RepoDirDeletedEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
    protected virtual ValueTask OnRepoScanRootRemovedEventAsync(RepoScanRootRemovedEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
    protected virtual ValueTask OnBootstrapEventAsync(BootstrapEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
    protected virtual ValueTask OnScanRunFinalisedEventAsync(ScanRunFinalisedEvent evt, CancellationToken ct) => ValueTask.CompletedTask;
    protected virtual ValueTask OnScanRootMetaChangedEventAsync(ScanRootMetaChangedEvent evt, CancellationToken ct) => ValueTask.CompletedTask;

    protected virtual void OnEventProcessingError(Exception ex, RepoEvent evt) =>
        Console.Error.WriteLine($"[{GetType().Name}] Error handling {evt.GetType().Name}: {ex}");

    /// <summary>
    /// Call this from derived class once the bootstrap event is fully processed.
    /// </summary>
    private void SignalReady()
        => _readyTcs.TrySetResult();

    public async Task WhenReadyAsync(CancellationToken ct = default)
    {
        if (!ct.CanBeCanceled)
        {
            await _readyTcs.Task.ConfigureAwait(false);
            return;
        }

        await using var reg = ct.Register(() => _readyTcs.TrySetCanceled(ct));
        await _readyTcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Wait until this plugin has processed at least the specified repo generation.
    /// Intended for coordination (e.g., UI refresh only after indexes are rebuilt).
    /// </summary>
    public Task WhenProcessedGenerationAsync(long generation, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _lastProcessedGeneration) >= generation)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GenerationWaiter waiter = new(generation, tcs);

        lock (_processedSync)
        {
            if (_lastProcessedGeneration >= generation)
                return Task.CompletedTask;

            _generationWaiters.Add(waiter);
        }

        if (!ct.CanBeCanceled)
            return tcs.Task;

        return WaitWithCancellationAsync(waiter, ct);
    }

    private async Task WaitWithCancellationAsync(GenerationWaiter waiter, CancellationToken ct)
    {
        await using var reg = ct.Register(() => waiter.Tcs.TrySetCanceled(ct));
        await waiter.Tcs.Task.ConfigureAwait(false);
    }

    private void UpdateLastProcessedGeneration(long generation)
    {
        List<GenerationWaiter>? toRelease = null;

        lock (_processedSync)
        {
            if (generation <= _lastProcessedGeneration)
                return;

            _lastProcessedGeneration = generation;

            if (_generationWaiters.Count == 0)
                return;

            for (var i = _generationWaiters.Count - 1; i >= 0; i--)
            {
                var waiter = _generationWaiters[i];
                if (waiter.TargetGeneration <= generation)
                {
                    toRelease ??= new List<GenerationWaiter>();
                    toRelease.Add(waiter);
                    _generationWaiters.RemoveAt(i);
                }
            }
        }

        if (toRelease is null)
            return;

        foreach (var waiter in toRelease)
            waiter.Tcs.TrySetResult();
    }

    private readonly record struct GenerationWaiter(long TargetGeneration, TaskCompletionSource Tcs);

    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

    public virtual async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _channel.Writer.TryComplete();
        await _workerTask.ConfigureAwait(false);

        lock (_processedSync)
        {
            foreach (var waiter in _generationWaiters)
                waiter.Tcs.TrySetCanceled();
            _generationWaiters.Clear();
        }
    }
}
