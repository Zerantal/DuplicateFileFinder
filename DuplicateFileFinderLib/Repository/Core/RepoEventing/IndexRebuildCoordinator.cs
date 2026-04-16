using DuplicateFileFinderLib.Repository.Infrastructure;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinderLib.Repository.Core.RepoEventing;

internal sealed class IndexRebuildCoordinator
    : ChannelWorker<IndexRebuildCoordinator.TrackedGeneration>, IRepoEventSink
{
    private readonly IReadOnlyList<IIndexGenerationBarrier> _barriers;
    private readonly Action<long, long?> _onIndexesRebuilt;
    private readonly GenerationBarrierState _completedGeneration;

    public IndexRebuildCoordinator(
        IReadOnlyList<IIndexGenerationBarrier> barriers,
        long initialCompletedGeneration,
        Action<long, long?> onIndexesRebuilt,
        int capacity = 256)
        : base(capacity)
    {
        _barriers = barriers;
        _onIndexesRebuilt = onIndexesRebuilt;
        _completedGeneration = new GenerationBarrierState(initialCompletedGeneration);
    }

    public void Post(RepoEvent evt)
    {
        if (evt is not IndexGenerationTrackedEvent tracked)
            return;

        if (tracked.Generation <= _completedGeneration.LastCompletedGeneration)
            return;

        TryPost(new TrackedGeneration(tracked.Generation, tracked.ScanRootId));
    }

    public Task WhenIndexesRebuiltAsync(long generation, CancellationToken ct = default)
        => _completedGeneration.WaitAsync(generation, ct);

    protected override async ValueTask ProcessItemAsync(TrackedGeneration tracked, CancellationToken ct)
    {
        foreach (var barrier in _barriers)
            await barrier.WhenProcessedGenerationAsync(tracked.Generation, ct).ConfigureAwait(false);

        _completedGeneration.CompleteThrough(tracked.Generation);
        _onIndexesRebuilt(tracked.Generation, tracked.ScanRootId);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _completedGeneration.CancelAll();
        return ValueTask.CompletedTask;
    }

    internal readonly record struct TrackedGeneration(long Generation, long? ScanRootId);
}
