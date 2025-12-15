using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage;
using MemoryPack;

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


    private async Task PersistScanRootSnapshotAsync(
        long scanRootId,
        IReadOnlyDictionary<long, DirRecord> allDirs,
        IReadOnlyDictionary<long, FileRecord> allFiles,
        CancellationToken ct = default)
    {
        // Find the ScanRoot
        var scanRoot = _metaFile.ScanRoots.FirstOrDefault(r => r.RootId == scanRootId);
        if (scanRoot is null)
            throw new InvalidOperationException($"Unknown ScanRoot {scanRootId}.");

        // Collect all DirIds under this ScanRoot.DirId (subtree)
        var dirsById = allDirs; // shorthand

        var dirIds = CollectDirSubtree(scanRoot.DirId, dirsById);

        var dirRecords = dirIds.Select(id => dirsById[id]).ToArray();

        // Collect all files whose DirId is in that subtree
        var filesByDir = allFiles.Values
            .GroupBy(f => f.DirId)
            .ToDictionary(g => g.Key, g => g.ToArray());

        var fileList = new List<FileRecord>();
        foreach (var dirId in dirIds)
            if (filesByDir.TryGetValue(dirId, out var filesInDir))
                fileList.AddRange(filesInDir);

        var rootSnap = new ScanRootSnapshot
        {
            ScanRootId = scanRootId,
            Dirs = dirRecords,
            Files = fileList.ToArray()
        };

        await RepoStore.SaveScanRootSnapshotAsync(_repoPath, rootSnap, ct).ConfigureAwait(false);
    }

    private static HashSet<long> CollectDirSubtree(
        long rootDirId,
        IReadOnlyDictionary<long, DirRecord> allDirs)
    {
        var result = new HashSet<long>();

        // Root not present? Nothing to do.
        if (!allDirs.ContainsKey(rootDirId))
            return result;

        // Build parent -> children index once for this call.
        // This is O(N) over allDirs and avoids N * N scanning.
        var childrenByParent = new Dictionary<long, List<long>>(allDirs.Count);

        foreach (var dir in allDirs.Values)
            if (dir.ParentDirId is { } parentId)
            {
                if (!childrenByParent.TryGetValue(parentId, out var list))
                {
                    list = new List<long>();
                    childrenByParent[parentId] = list;
                }

                list.Add(dir.DirId);
            }

        var queue = new Queue<long>();
        result.Add(rootDirId);
        queue.Enqueue(rootDirId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!childrenByParent.TryGetValue(current, out var children))
                continue;

            for (var i = 0; i < children.Count; i++)
            {
                var childId = children[i];
                if (result.Add(childId))
                    queue.Enqueue(childId);
            }
        }

        return result;
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
            if (snap is null) continue;

            foreach (var d in snap.Dirs)
                _dirs[d.DirId] = d;

            foreach (var f in snap.Files)
                _files[f.FileId] = f;
        }

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

        // Take copies so we snapshot a stable view
        var dirsCopy = new Dictionary<long, DirRecord>(_dirs);
        var filesCopy = new Dictionary<long, FileRecord>(_files);

        // Persist snapshots per scan root
        foreach (var scanRoot in _scanRoots.Values)
            PersistScanRootSnapshotAsync(
                    scanRoot.RootId,
                    dirsCopy,
                    filesCopy,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
    }
}