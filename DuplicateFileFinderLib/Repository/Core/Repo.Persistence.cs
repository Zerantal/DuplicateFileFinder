using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage;
using MemoryPack;
using PackedStringPool = DuplicateFileFinderLib.Repository.Storage.Models.PackedStringPool;
using ScanRootSnapshotV2 = DuplicateFileFinderLib.Repository.Storage.Models.ScanRootSnapshotV2;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    private void ReplayDeltas()
    {
        if (!Directory.Exists(_logDirPath))
            return;

        var generation = Meta.Generation;
        var baseline = Meta.LastSnapshottedLogSequence;

        var pattern = $"{generation}-*.delta";

        var files = Directory
            .GetFiles(_logDirPath, pattern)
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<logId>"
            var dash = name.IndexOf('-');
            if (dash <= 0) continue;

            var idPart = name[(dash + 1)..];
            if (!long.TryParse(idPart, out var logId))
                continue;

            if (logId <= baseline)
                continue;

            var bytes = File.ReadAllBytes(path);
            var delta = MemoryPackSerializer.Deserialize<RepoDelta>(bytes);
            if (delta is not null)
                ApplyDelta_NoLock(delta);
        }
    }

    private void ApplyDelta_NoLock(RepoDelta delta)
    {
        // dirs
        foreach (var d in delta.Dirs)
        {
            if (d.Status == ScanEntryStatus.Deleted)
                _dirs.Remove(d.DirId);
            else
                _dirs[d.DirId] = d;
        }

        // files
        foreach (var f in delta.Files)
        {
            if (f.Status == ScanEntryStatus.Deleted)
                _files.Remove(f.FileId);
            else
                _files[f.FileId] = f;
        }

    }


    private void DeleteObsoleteDeltas_NoLock()
    {
        if (!Directory.Exists(_logDirPath)) return;

        foreach (var path in Directory.GetFiles(_logDirPath, $"{Meta.Generation}-*.delta"))
        {
            var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<seq>"
            var dash = name.IndexOf('-');
            if (dash <= 0) continue;
            var seqPart = name[(dash + 1)..];
            if (long.TryParse(seqPart, out var seq) && seq <= Meta.LastSnapshottedLogSequence)
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // tolerate
                }
        }
    }

    // ------------------ new repo store ------------------------

    private async Task PersistMetaAsync(CancellationToken ct = default)
    {
        await RepoStore.SaveMetaAsync(_repoPath, _metaFile, ct).ConfigureAwait(false);
    }

    private void SyncMetaFile_NoLock()
    {
        // Ensure _metaFile mirrors the current in-memory state (_meta, _scanRoots, _scanRuns)
        _metaFile = new RepoMetaFile
        {
            Meta = Meta,
            ScanRoots = _scanRoots.Values.ToList(),
            ScanRuns = _scanRuns.ToList()
        };
    }

    private static readonly PackedStringPool EmptyStringPool
        = new PackedStringPool(Array.Empty<byte>(), [0]);

    private static ScanRootSnapshotV2 CreateEmptySnapshotV2(long scanRootId)
        => new ScanRootSnapshotV2
        {
            ScanRootId = scanRootId,
            StringPool = EmptyStringPool,
            Dirs = [],
            Files = []
        };
    
    private Task PersistScanRootSnapshotV2Async(ScanRootSnapshotV2 snapshot, CancellationToken ct = default)
    {
        // single place to write V2 snapshots
        return RepoStore.SaveScanRootSnapshotV2Async(_repoPath, snapshot, ct);
    }

    // ---------- util ----------

    private static void Fsync(string path)
    {
        using var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        fs.Flush(true);
    }

    // ReSharper disable once UnusedMember.Local
    private (long logBytes, int count) GetLogSizeAndCount()
    {
        if (!Directory.Exists(_logDirPath)) return (0L, 0);

        long bytes = 0;
        var count = 0;
        foreach (var p in Directory.GetFiles(_logDirPath, $"{Meta.Generation}-*.delta"))
        {
            var fi = new FileInfo(p);
            if (fi.Exists)
            {
                bytes += fi.Length;
                count++;
            }
        }

        return (bytes, count);
    }

    private async Task InitialiseStateFromStoreAsync(CancellationToken ct)
    {
        _dirs.Clear();
        _files.Clear();
        _dirPathCache.Clear();

        // 1. Load per-root snapshots
        foreach (var root in _scanRoots.Values)
        {
            ct.ThrowIfCancellationRequested();

            var snap = await RepoStore.LoadScanRootSnapshotAsync(_repoPath, root.RootId, ct)
                .ConfigureAwait(false);
            
            if (snap.oldSnapshot is null) continue;
            foreach (var d in snap.oldSnapshot.Dirs)
                _dirs[d.DirId] = d;

            foreach (var f in snap.oldSnapshot.Files)
                _files[f.FileId] = f;

            if (snap.newSnapshot is null) continue;
            _scanRootSnapshots[root.RootId] = snap.newSnapshot.Value;
        }
        RebuildDirHandleMap_NoLock();
        ReplayDeltas();
    }
    
    // Writes per-root snapshots and updates meta. Caller must hold _sync.
    private void SaveScanSnapshots_NoLock()
    {
        var lastSnapLog = Meta.NextLogSequence - 1; // -1 when no logs yet

        // Advance meta baseline
        Meta = Meta with { LastSnapshottedLogSequence = lastSnapLog };

        // Persist updated meta (including roots/runs) via RepoStore
        SyncMetaFile_NoLock();
        _ = PersistMetaAsync();

        // Snapshot a stable view of roots + snapshots
        var rootsCopy = _scanRoots.Values.ToArray();
        var snapsCopy = new Dictionary<long, ScanRootSnapshotV2>(_scanRootSnapshots);

        // Persist snapshots per scan root
        foreach (var root in rootsCopy)
        {
            if (root.IsDeleted)
            {
                RepoStore.DeleteScanRootSnapshotAsync(_repoPath, root.RootId, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                continue;
            }

            if (snapsCopy.TryGetValue(root.RootId, out var snapV2))
            {
                PersistScanRootSnapshotV2Async(snapV2, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                // No snapshot in memory. Ensure no stale file exists on disk.
                RepoStore.DeleteScanRootSnapshotAsync(_repoPath, root.RootId, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
        }
    }
}