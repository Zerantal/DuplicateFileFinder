using System;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

// ReSharper disable UnassignedGetOnlyAutoProperty

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeRepoHost(IRepo repo) : IRepoHost
{
    public IRepo Repo { get; } = repo;
    public IFileDirReadModel FileDirIndex { get; set; } = new FakeFileDirReadModel();
    public long LastIndexedGeneration { get; private set; } = 1;

    public ITreeIndexReadModel TreeIndex { get; set; } = new FakeTreeIndex();
    public IHashIndexReadModel HashIndex { get; set; } = new FakeHashIndex();

    public event EventHandler<RepoIndexesRebuiltEventArgs>? IndexesRebuilt;

    public Task WhenIndexesRebuiltAsync(long generation, CancellationToken ct = default)
    {
        if (generation <= LastIndexedGeneration)
            return Task.CompletedTask;

        return Task.FromCanceled(ct.CanBeCanceled ? ct : new CancellationToken(true));
    }

    public void RaiseIndexesRebuilt(long generation = 1, long? scanRootId = 1)
    {
        LastIndexedGeneration = generation;
        IndexesRebuilt?.Invoke(this, new RepoIndexesRebuiltEventArgs(generation, scanRootId));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
