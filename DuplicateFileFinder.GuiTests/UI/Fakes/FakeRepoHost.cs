using System;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;

// ReSharper disable UnassignedGetOnlyAutoProperty

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeRepoHost(IRepo repo) : IRepoHost
{
    public IRepo Repo { get; } = repo;
    public IFileDirReadModel FileDirIndex { get; set; } = new FakeFileDirReadModel();
    public long LastIndexedGeneration { get; private set; } = 1;
    public ITreeIndexReadModel TreeIndex { get; } = new DummyTreeIndex();
    public IHashIndexReadModel HashIndex { get; } = new DummyHashIndex();

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

    private sealed class DummyTreeIndex : ITreeIndexReadModel
    {
        public DirAggregateStats GetDirStats(DirHandle dirId) => throw new NotImplementedException();

        public ReadOnlySpan<DirHandle> GetChildDirs(DirHandle dir) => throw new NotImplementedException();

        public ReadOnlySpan<FileHandle> GetChildFiles(DirHandle dir) => throw new NotImplementedException();
        public bool TryGetSubtreeRange(DirHandle dir, out SubtreeRange range) => throw new NotImplementedException();

        public bool TryGetFileDirPreorder(FileHandle file, out int preorder) => throw new NotImplementedException();
    }

    private sealed class DummyHashIndex : IHashIndexReadModel
    {
        public DuplicateGroupPage GetGroupsPage(in DuplicateQuery query, int offset, int count) =>
            throw new NotImplementedException();

        public DuplicateGroupPage
            GetGroupsPage(in DuplicateQuery query, in SubtreeFilter filter, int offset, int count) =>
            throw new NotImplementedException();

        public ReadOnlySpan<FileHandle> GetGroupFiles(in HashGroupDescriptor group) =>
            throw new NotImplementedException();

        public int TotalDuplicateFileCount { get; }
        public long TotalSpaceTakenByDuplicates { get; }
    }
}
