using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Infrastructure;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinderLib.Repository.Plugins;

public abstract class ChannelRepoPlugin(int capacity = 1024)
    : ChannelWorker<RepoEvent>(capacity), IRepoPlugin, IReadyState, IIndexGenerationBarrier
{
    private readonly TaskCompletionSource _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly GenerationBarrierState _processedGeneration = new();

    public void Post(RepoEvent evt) => TryPost(evt);

    protected virtual ValueTask HandleEventAsync(RepoEvent evt, CancellationToken ct)
        => ProcessItemAsync(evt, ct);

    protected sealed override ValueTask ProcessItemAsync(RepoEvent evt, CancellationToken ct)
        => HandleEventWithTimingAsync(evt, ct);

    private async ValueTask HandleEventWithTimingAsync(RepoEvent evt, CancellationToken ct)
    {
        using (TimingLog.Start($"Processing {evt.GetType().Name} ({GetType().Name})"))
        {
            switch (evt)
            {
                case BootstrapEvent bootstrap:
                    await OnBootstrapEventAsync(bootstrap, ct).ConfigureAwait(false);
                    SignalReady();
                    break;

                case ScanRunFinalisedEvent finalised:
                    await OnScanRunFinalisedEventAsync(finalised, ct).ConfigureAwait(false);
                    break;

                case ScanRootSnapshotReplacedEvent replaced:
                    await OnScanRootSnapshotReplacedEventAsync(replaced, ct).ConfigureAwait(false);
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
        }

        if (evt is IndexGenerationTrackedEvent tracked)
            _processedGeneration.CompleteThrough(tracked.Generation);
    }

    protected override void OnItemProcessingError(Exception ex, RepoEvent evt) =>
        OnEventProcessingError(ex, evt);

    protected virtual ValueTask OnScanRootSnapshotReplacedEventAsync(ScanRootSnapshotReplacedEvent evt,
        CancellationToken ct) => ValueTask.CompletedTask;

    protected virtual ValueTask OnRepoFileDeletedEventAsync(RepoFileDeletedEvent evt, CancellationToken ct) =>
        ValueTask.CompletedTask;

    protected virtual ValueTask OnRepoDirDeletedEventAsync(RepoDirDeletedEvent evt, CancellationToken ct) =>
        ValueTask.CompletedTask;

    protected virtual ValueTask OnRepoScanRootRemovedEventAsync(RepoScanRootRemovedEvent evt, CancellationToken ct) =>
        ValueTask.CompletedTask;

    protected virtual ValueTask OnBootstrapEventAsync(BootstrapEvent evt, CancellationToken ct) =>
        ValueTask.CompletedTask;

    protected virtual ValueTask OnScanRunFinalisedEventAsync(ScanRunFinalisedEvent evt, CancellationToken ct) =>
        ValueTask.CompletedTask;

    protected virtual ValueTask OnScanRootMetaChangedEventAsync(ScanRootMetaChangedEvent evt, CancellationToken ct) =>
        ValueTask.CompletedTask;

    protected virtual void OnEventProcessingError(Exception ex, RepoEvent evt) =>
        Console.Error.WriteLine($"[{GetType().Name}] Error handling {evt.GetType().Name}: {ex}");

    private void SignalReady() => _readyTcs.TrySetResult();

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

    public Task WhenProcessedGenerationAsync(long generation, CancellationToken ct = default)
        => _processedGeneration.WaitAsync(generation, ct);

    protected override async ValueTask DisposeAsyncCore()
    {
        _processedGeneration.CancelAll();
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}
