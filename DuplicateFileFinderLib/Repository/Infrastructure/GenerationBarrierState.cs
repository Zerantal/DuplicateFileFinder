namespace DuplicateFileFinderLib.Repository.Infrastructure;

internal sealed class GenerationBarrierState
{
    private readonly Lock _sync = new();
    private long _lastCompletedGeneration;
    private readonly List<GenerationWaiter> _waiters = new();

    public GenerationBarrierState(long initialCompletedGeneration = 0)
    {
        _lastCompletedGeneration = initialCompletedGeneration;
    }

    public long LastCompletedGeneration => Volatile.Read(ref _lastCompletedGeneration);

    public Task WaitAsync(long generation, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _lastCompletedGeneration) >= generation)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = new GenerationWaiter(generation, tcs);

        lock (_sync)
        {
            if (_lastCompletedGeneration >= generation)
                return Task.CompletedTask;

            _waiters.Add(waiter);
        }

        if (!ct.CanBeCanceled)
            return tcs.Task;

        return WaitWithCancellationAsync(waiter, ct);
    }

    public void CompleteThrough(long generation)
    {
        List<GenerationWaiter>? toRelease = null;

        lock (_sync)
        {
            if (generation <= _lastCompletedGeneration)
                return;

            _lastCompletedGeneration = generation;

            if (_waiters.Count == 0)
                return;

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (waiter.TargetGeneration <= generation)
                {
                    toRelease ??= [];
                    toRelease.Add(waiter);
                    _waiters.RemoveAt(i);
                }
            }
        }

        if (toRelease is null)
            return;

        foreach (var waiter in toRelease)
            waiter.Tcs.TrySetResult();
    }

    public void CancelAll()
    {
        lock (_sync)
        {
            foreach (var waiter in _waiters)
                waiter.Tcs.TrySetCanceled();

            _waiters.Clear();
        }
    }

    private static async Task WaitWithCancellationAsync(GenerationWaiter waiter, CancellationToken ct)
    {
        await using var reg = ct.Register(() => waiter.Tcs.TrySetCanceled(ct));
        await waiter.Tcs.Task.ConfigureAwait(false);
    }

    private readonly record struct GenerationWaiter(long TargetGeneration, TaskCompletionSource Tcs);
}
