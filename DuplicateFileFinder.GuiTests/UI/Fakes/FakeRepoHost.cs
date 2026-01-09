using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.GuiTests.Ui.Fakes;

public sealed class FakeRepoHost : IRepoHost
{
    public IRepo Repo { get; }
    public IHashIndexReadModel HashIndex { get; }
    public ITreeIndexReadModel TreeIndex { get; }
    public IFileDirReadModel FileDirIndex { get; }

    public event EventHandler<RepoIndexesRebuiltEventArgs>? IndexesRebuilt;

    public FakeRepoHost()
    {
        Repo = new FakeRepo();
        HashIndex = new FakeHashIndexReadModel();
        TreeIndex = new FakeTreeIndexReadModel();
        FileDirIndex = new FakeFileDirReadModel();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void RaiseIndexesRebuilt(long generation = 1, long? scanRootId = null)
        => IndexesRebuilt?.Invoke(this, new RepoIndexesRebuiltEventArgs(generation, scanRootId));

    private sealed class FakeRepo : IRepo
    {
        public Task DeleteScanRootAsync(long scanRootId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScanRootDisplayNameAsync(long scanRootId, string? displayName, CancellationToken ct = default) => Task.CompletedTask;

        public IReadOnlyList<ScanRun> ScanRunsView { get; } = Array.Empty<ScanRun>();
        public IReadOnlyList<ScanRoot> ScanRootsView { get; } = Array.Empty<ScanRoot>();

        public ScanRootSnapshotView? TryGetScanRootView(long scanRootId) => null;

        public RepoSnapshotView GetRepoSnapshotView()
        {
            return new RepoSnapshotView
            {
                Snapshots = new Dictionary<long, ScanRootSnapshotView>(),
                ScanRoots = new Dictionary<long, ScanRoot>()
            };
        }

        public bool HasScanCheckpoint(long scanRootId) => false;

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHashIndexReadModel : IHashIndexReadModel
    {
        public IReadOnlyList<(long size, IReadOnlyList<FileHandle> list)> GetDuplicateGroups(int minDuplicates = 2, long minSize = 1)
            => Array.Empty<(long, IReadOnlyList<FileHandle>)>();

        public int TotalDuplicateFileCount => 0;
        public long TotalSpaceTakenByDuplicates => 0;
    }

    private sealed class FakeTreeIndexReadModel : ITreeIndexReadModel
    {
        public ImmutableArray<DirHandle> GetChildDirs(DirHandle dir) => ImmutableArray<DirHandle>.Empty;
        public ImmutableArray<FileHandle> GetChildFiles(DirHandle dir) => ImmutableArray<FileHandle>.Empty;

        public DirAggregateStats GetDirStats(DirHandle dirId) => null!;
    }

    private sealed class FakeFileDirReadModel : IFileDirReadModel
    {
        public bool TryGetDir(long dirId, out DirHandle handle) { handle = DirHandle.Invalid; return false; }
        public bool TryGetFile(long fileId, out FileHandle handle) { handle = default; return false; }

        public int FileCount => 0;
        public int DirCount => 0;

        public bool TryGetFilePathById(long fileId, out string relativePath) { relativePath = ""; return false; }
        public bool TryGetFilePathByHandle(FileHandle fileHandle, out string relativePath) { relativePath = ""; return false; }
        public bool TryGetDirPathById(long dirId, out string relativePath) { relativePath = ""; return false; }
        public bool TryGetDirPathByHandle(DirHandle dirHandle, out string relativePath) { relativePath = ""; return false; }
    }
}
