using System;
using System.Collections.Generic;
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
    public ITreeIndexReadModel TreeIndex { get; } = new DummyTreeIndex();
    public IHashIndexReadModel HashIndex { get; } = new DummyHashIndex();

    public event EventHandler<RepoIndexesRebuiltEventArgs>? IndexesRebuilt;

    public void RaiseIndexesRebuilt()
        => IndexesRebuilt?.Invoke(this, new RepoIndexesRebuiltEventArgs(1, 1));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class DummyTreeIndex : ITreeIndexReadModel
    {
        public DirAggregateStats GetDirStats(DirHandle dirId) => throw new NotImplementedException();

        public ReadOnlySpan<DirHandle> GetChildDirs(DirHandle dir) => throw new NotImplementedException();

        public ReadOnlySpan<FileHandle> GetChildFiles(DirHandle dir) => throw new NotImplementedException();
    }
    private sealed class DummyHashIndex : IHashIndexReadModel
    {
        public IReadOnlyList<(long size, IReadOnlyList<FileHandle> list)>
            GetDuplicateGroups(int minDuplicates = 2, long minSize = 1) =>
            new List<(long size, IReadOnlyList<FileHandle> list)>();

        public int TotalDuplicateFileCount { get; }
        public long TotalSpaceTakenByDuplicates { get; }
    }
}
