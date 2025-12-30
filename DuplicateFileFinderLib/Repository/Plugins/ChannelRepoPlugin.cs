// DuplicateFileFinderLib/Repository/Plugins/ChannelRepoPlugin.cs

using System.Threading.Channels;
using DuplicateFileFinderLib.Logging;
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
    
    protected virtual ValueTask HandleEventAsync(RepoEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case BootstrapEvent bootstrap:
                using (TimingLog.Start($"Processing BootstrapEvent ({GetType().Name})"))
                {
                    OnBootstrapEvent(bootstrap);
                }
                SignalReady();
                break;

            case ScanRunFinalisedEvent finalised:
                OnScanRunFinalisedEvent(finalised);
                break;

            case ScanRootSnapshotCommittedEvent snapCommitted:
                using (TimingLog.Start($"Processing OnScanRootSnapshotCommittedEvent ({GetType().Name})"))
                {
                    OnScanRootSnapshotCommittedEvent(snapCommitted);
                }
                break;
        }

        // Record that we have fully processed this event's generation.
        // This enables callers (e.g., RepoHost) to wait until indexes have rebuilt.
        UpdateLastProcessedGeneration(evt.Generation);

        return ValueTask.CompletedTask;
    }

    protected virtual void OnBootstrapEvent(BootstrapEvent evt) { }
    protected virtual void OnScanRunFinalisedEvent(ScanRunFinalisedEvent evt) { }
    protected virtual void OnScanRootSnapshotCommittedEvent(ScanRootSnapshotCommittedEvent evt) { }

    protected virtual void OnEventProcessingError(Exception ex, RepoEvent evt)
    {
        Console.Error.WriteLine($"[{GetType().Name}] Error handling {evt.GetType().Name}: {ex}");
    }

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

    public async ValueTask DisposeAsync()
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
