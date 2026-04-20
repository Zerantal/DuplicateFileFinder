using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;
// ReSharper disable UnassignedGetOnlyAutoProperty
// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable CollectionNeverUpdated.Local

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeRepo(IEnumerable<ScanRoot>? scanRoots = null) : IRepo
{
    private List<ScanRoot> _scanRoots = scanRoots?.ToList() ?? [];
    private readonly List<ScanRun> _scanRuns = [];

    public Dictionary<string, object> ReturnResultFor { get; } = new();

    public RepoSnapshotView? SnapshotToReturn { get; set; }

    private T? Result<T>(T? defaultValue = default, [CallerMemberName] string memberName = "")
    {
        if (!ReturnResultFor.TryGetValue(memberName, out var result))
        {
            return defaultValue;
        }
        return (T?)result;
    }



    public Task<long> DeleteScanRootAsync(ScanRootId scanRootId, CancellationToken ct) => throw new NotImplementedException();

    public Task SetScanRootDisplayNameAsync(ScanRootId scanRootId, string? displayName, CancellationToken ct)
        => throw new NotImplementedException();

    public IReadOnlyList<ScanRun> ScanRunsView => _scanRuns;
    public IReadOnlyList<ScanRoot> ScanRootsView => _scanRoots;

    public ScanRootSnapshotView? TryGetScanRootView(ScanRootId scanRootId)
    {
        if (SnapshotToReturn?.Snapshots is null)
            return null;

        return SnapshotToReturn.Snapshots.GetValueOrDefault(scanRootId);
    }

    public RepoSnapshotView GetRepoSnapshotView()
    {
        if (SnapshotToReturn is not null)
            return SnapshotToReturn;

        return new RepoSnapshotView { Snapshots = null!, ScanRoots = null! };
    }

    public HashSet<ScanRootId> ScanRootsWithCheckpoints { get; } = [];
    public Func<ScanRootId, bool>? HasScanCheckpointImpl { get; set; }
    public bool HasScanCheckpoint(ScanRootId scanRootId)
    {
        if (HasScanCheckpointImpl is not null)
            return HasScanCheckpointImpl(scanRootId);

        return ScanRootsWithCheckpoints.Contains(scanRootId);
    }

    Task<DeleteResult> IRepo.DeleteFileAsync(FileHandle file, CancellationToken ct)
    {
        DeletedFiles.Add(file);

        return Task.FromResult(Result(
            defaultValue: DeleteResult.Ok(1, 1, 1, 0)));
    }

    Task<DeleteResult> IRepo.DeleteDirAsync(DirHandle dir, CancellationToken ct)
    {
        DeletedDirs.Add(dir);

        return Task.FromResult(Result(
            defaultValue: DeleteResult.Ok(1, 1, 0, 1)));

    }

    public readonly List<FileHandle> DeletedFiles = [];
    public readonly List<DirHandle> DeletedDirs = [];

    public void SetScanRoots(IEnumerable<ScanRoot> scanRoots)
        => _scanRoots = scanRoots.ToList();

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
