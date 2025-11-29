using System.Text.Json;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository;

public sealed partial class Repo
{
    // file/dir names 
    private readonly string _repoPath;
    private readonly string _metaFilePath;
    private readonly string _snapshotFilePath;
    private readonly string _logDirPath;
    private readonly string _scanRunsFilePath;
    private readonly string _scanRootsFilePath;

    private RepoMetaFile _metaFile;

    private void LoadMetaOrCreateFresh()
    {
        if (!File.Exists(_metaFilePath))
        {
            // First time creating a repo → initialise everything
            _meta = new RepoMeta
            {
                SchemaVersion = RepoSchemaVersion,
                Generation = 1,
                NextLogSequence = 0,
                LastSnapshottedLogSequence = -1,
                LastCompaction = DateTimeOffset.UtcNow,
                RepoId = Guid.NewGuid(),
                RepoPath = _repoPath,
                RepoHostName = Environment.MachineName,
                NextScanSequence = 0
            };

            SaveMeta_NoLock();
            return;
        }

        // Load existing
        _meta = JsonSerializer.Deserialize<RepoMeta>(File.ReadAllText(_metaFilePath))
                ?? throw new InvalidDataException("Failed to load RepoMeta.");
    }
    
    private void SaveMeta_NoLock()
    {
        var json = JsonSerializer.Serialize(_meta, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_metaFilePath, json);
        Fsync(_metaFilePath);
    }
    
// Writes snapshot + indexes and updates meta. Caller must hold _sync.
    private void SaveSnapshot_NoLock()
    {
        var lastSnapLog = _meta.NextLogSequence - 1; // -1 when no logs yet

        // Only update LastSnapshottedLogSequence; SchemaVersion is managed elsewhere.
        _meta = _meta with { LastSnapshottedLogSequence = lastSnapLog };

        var snapshot = new RepoSnapshot
        {
            Meta      = _meta,
            Files     = _files,
            Dirs      = _dirs,
            HashIndex = _hashIndex,
            ScanRuns  = _scanRuns,
            ScanRoots =  _scanRoots.Values.ToList(),
        };

        var tmp = _snapshotFilePath + ".tmp";
        var bytes = MemoryPackSerializer.Serialize(snapshot);
        File.WriteAllBytes(tmp, bytes);
        Fsync(tmp);
        File.Move(tmp, _snapshotFilePath, true);

        SaveMeta_NoLock();
    }
    
    private void LoadSnapshot()
    {
        _files.Clear();
        _dirs.Clear();
        _hashIndex.Clear();
        _scanRuns.Clear();
        _scanRunIndex.Clear();
        _dirPathCache.Clear();

        if (!File.Exists(_snapshotFilePath)) return;

        var bytes = File.ReadAllBytes(_snapshotFilePath);

        try
        {
            var snapshot = MemoryPackSerializer.Deserialize<RepoSnapshot>(bytes);
            if (snapshot is not null)
            {
                // Optional: sanity checks
                // if (snapshot._meta.RepoId != _meta.RepoId) throw ...
                // if (snapshot._meta.Generation != _meta.Generation) throw ...

                _files = snapshot.Files;
                _dirs = snapshot.Dirs;
                _hashIndex = snapshot.HashIndex;
                _scanRuns = snapshot.ScanRuns;

                _scanRunIndex.Clear();
                foreach (var run in _scanRuns)
                    _scanRunIndex[run.ScanSequence] = run;
            }
        }
        catch (MemoryPackSerializationException)
        {
            Log.Error("Failed to load repo snapshot.");
            throw;
        }
    }
    
    private void LoadScanRoots()
    {
        if (!File.Exists(_scanRootsFilePath))
            return;

        var json = File.ReadAllText(_scanRootsFilePath);
        _scanRoots = JsonSerializer.Deserialize<Dictionary<Guid, ScanRoot>>(json) ?? new Dictionary<Guid, ScanRoot>();
    }

    private void SaveScanRoots_NoLock()
    {
        var json = JsonSerializer.Serialize(_scanRoots, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_scanRootsFilePath, json);
        Fsync(_scanRootsFilePath);
    }
    
    // Load persisted scan runs from scanruns.json (if present) and
    // overlay them on top of whatever was loaded from the snapshot.
    private void LoadScanRuns()
    {
        if (!File.Exists(_scanRunsFilePath))
            return; // Keep whatever ScanRuns came from the snapshot

        var json = File.ReadAllText(_scanRunsFilePath);
        var fromFile = JsonSerializer.Deserialize<List<ScanRun>>(json) ?? new List<ScanRun>();

        // Merge: snapshot data is baseline; scanruns.json overrides / adds by ScanSequence.
        var bySeq = new Dictionary<long, ScanRun>();
        foreach (var run in _scanRuns)
            bySeq[run.ScanSequence] = run;

        foreach (var run in fromFile)
            bySeq[run.ScanSequence] = run;

        _scanRuns = bySeq.Values.OrderBy(r => r.ScanSequence).ToList();

        _scanRunIndex.Clear();
        foreach (var run in _scanRuns)
            _scanRunIndex[run.ScanSequence] = run;
    }
    
    private void SaveScanRuns_NoLock()
    {
        var json = JsonSerializer.Serialize(_scanRuns, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_scanRunsFilePath, json);
        Fsync(_scanRunsFilePath);
    }
    
    private void ReplayDeltas()
    {
        if (!Directory.Exists(_logDirPath)) return;

        var files = Directory.GetFiles(_logDirPath, $"{_meta.Generation}-*.delta")
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<logId>"
            var dash = name.IndexOf('-');
            if (dash <= 0) continue;

            var idPart = name[(dash + 1)..];
            if (long.TryParse(idPart, out var logId))
                // skip deltas already covered by snapshot
                if (logId <= _meta.LastSnapshottedLogSequence)
                    continue;

            var bytes = File.ReadAllBytes(path);
            var delta = MemoryPackSerializer.Deserialize<RepoDelta>(bytes);
            if (delta != null) ApplyDelta(delta);
        }
    }

    private void ApplyDelta(RepoDelta delta)
    {
        // Upserts / updates
        foreach (var f in delta.Files)
        {
            // If an existing file's hash changed, remove from old hash bucket
            if (_files.TryGetValue(f.Id, out var existing))
            {
                if (f.Hash == HashKey.NotComputed || f.Hash == HashKey.CannotCompute)
                    continue;
                if (!existing.Hash.Equals(f.Hash))
                {
                    if (_hashIndex.TryGetValue(existing.Hash, out var oldList))
                    {
                        oldList.Remove(f.Id);
                        if (oldList.Count == 0)
                            _hashIndex.Remove(existing.Hash);
                    }
                }
            }

            _files[f.Id] = f;

            // don't add to hash index when hash value hasn't been calculated
            if (f.Hash == HashKey.NotComputed || f.Hash == HashKey.CannotCompute)
                continue;
            
            if (!_hashIndex.TryGetValue(f.Hash, out var list))
            {
                list = new List<Guid>(4);
                _hashIndex[f.Hash] = list;
            }

            // guard against dup if same delta is re-applied
            if (list.Count == 0 || list[^1] != f.Id)
                if (!list.Contains(f.Id))
                    list.Add(f.Id);
        }

        foreach (var d in delta.Dirs)
        {
            _dirs[d.Id] = d;
            // Invalidate cached path; will be recomputed on next GetFullDirPath
            _dirPathCache.Remove(d.Id, out _);
        }

        // Deletions (tombstones)
        if (delta.DeletedFiles is { Count: > 0 })
            foreach (var tomb in delta.DeletedFiles)
            {
                if (!_files.TryGetValue(tomb.Id, out var file))
                    continue;
                
                // Remove from hash index
                if (_hashIndex.TryGetValue(file.Hash, out var list))
                {
                    list.Remove(tomb.Id);
                    if (list.Count == 0)
                        _hashIndex.Remove(file.Hash);
                }
                
                _files.Remove(tomb.Id);
            }

        if (delta.DeletedDirs is { Count: > 0 })
            foreach (var tomb in delta.DeletedDirs)
            {
                _dirs.Remove(tomb.Id);
                _dirPathCache.Remove(tomb.Id, out _);
            }
    }
    
    private void DeleteObsoleteDeltas_NoLock()
    {
        if (!Directory.Exists(_logDirPath)) return;

        foreach (var path in Directory.GetFiles(_logDirPath, $"{_meta.Generation}-*.delta"))
        {
            var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<seq>"
            var dash = name.IndexOf('-');
            if (dash <= 0) continue;
            var seqPart = name[(dash + 1)..];
            if (long.TryParse(seqPart, out var seq) && seq <= _meta.LastSnapshottedLogSequence)
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
            Meta      = _meta,
            ScanRoots = _scanRoots.Values.ToList(),
            ScanRuns  = _scanRuns.ToList()
        };
    }

    
    private async Task PersistScanRootSnapshotAsync(
        Guid scanRootId,
        IReadOnlyDictionary<Guid, DirRecord> allDirs,
        IReadOnlyDictionary<Guid, FileRecord> allFiles,
        CancellationToken ct = default)
    {
        // Find the ScanRoot
        var scanRoot = _metaFile.ScanRoots.FirstOrDefault(r => r.Id == scanRootId);
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
        {
            if (filesByDir.TryGetValue(dirId, out var filesInDir))
                fileList.AddRange(filesInDir);
        }

        var rootSnap = new ScanRootSnapshotOnDisk 
        {
            ScanRootId = scanRootId,
            Dirs  = dirRecords,
            Files = fileList.ToArray()
        };

        await RepoStore.SaveScanRootSnapshotAsync(_repoPath, rootSnap, ct).ConfigureAwait(false);
    }

    // private static HashSet<Guid> CollectDirSubtree(
    //     Guid rootDirId,
    //     IReadOnlyDictionary<Guid, DirRecord> allDirs)
    // {
    //     var result = new HashSet<Guid> { rootDirId };
    //     var queue = new Queue<Guid>();
    //     queue.Enqueue(rootDirId);
    //
    //     while (queue.Count > 0)
    //     {
    //         var current = queue.Dequeue();
    //
    //         foreach (var dir in allDirs.Values)
    //         {
    //             if (dir.ParentId is Guid parentId &&
    //                 parentId == current &&
    //                 result.Add(dir.Id))
    //             {
    //                 queue.Enqueue(dir.Id);
    //             }
    //         }
    //     }
    //
    //     return result;
    // }
    
    private static HashSet<Guid> CollectDirSubtree(
        Guid rootDirId,
        IReadOnlyDictionary<Guid, DirRecord> allDirs)
    {
        var result = new HashSet<Guid>();

        // Root not present? Nothing to do.
        if (!allDirs.ContainsKey(rootDirId))
            return result;

        // Build parent -> children index once for this call.
        // This is O(N) over allDirs and avoids N * N scanning.
        var childrenByParent = new Dictionary<Guid, List<Guid>>(allDirs.Count);

        foreach (var dir in allDirs.Values)
        {
            if (dir.ParentId is Guid parentId)
            {
                if (!childrenByParent.TryGetValue(parentId, out var list))
                {
                    list = new List<Guid>();
                    childrenByParent[parentId] = list;
                }

                list.Add(dir.Id);
            }
        }

        var queue = new Queue<Guid>();
        result.Add(rootDirId);
        queue.Enqueue(rootDirId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (!childrenByParent.TryGetValue(current, out var children))
                continue;

            for (int i = 0; i < children.Count; i++)
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

    private (long logBytes, int count) GetLogSizeAndCount()
    {
        if (!Directory.Exists(_logDirPath)) return (0L, 0);

        long bytes = 0;
        var count = 0;
        foreach (var p in Directory.GetFiles(_logDirPath, $"{_meta.Generation}-*.delta"))
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
}