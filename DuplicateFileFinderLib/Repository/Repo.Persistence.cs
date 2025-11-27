using System.Text.Json;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository;

public sealed partial class Repo : IRepo
{
    // file/dir names 
    private readonly string _metaFile;
    private readonly string _snapshotFile;
    private readonly string _logDir;
    private readonly string _scanRunsFile;
    private readonly string _scanRootsFile;

    private void LoadMetaOrCreateFresh(string repoPath)
    {
        if (!File.Exists(_metaFile))
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
                RepoPath = repoPath,
                RepoHostName = Environment.MachineName,
                NextScanSequence = 0
            };

            SaveMeta_NoLock();
            return;
        }

        // Load existing
        _meta = JsonSerializer.Deserialize<RepoMeta>(File.ReadAllText(_metaFile))
                ?? throw new InvalidDataException("Failed to load RepoMeta.");
    }
    
    private void SaveMeta_NoLock()
    {
        var json = JsonSerializer.Serialize(_meta, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_metaFile, json);
        Fsync(_metaFile);
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

        var tmp = _snapshotFile + ".tmp";
        var bytes = MemoryPackSerializer.Serialize(snapshot);
        File.WriteAllBytes(tmp, bytes);
        Fsync(tmp);
        File.Move(tmp, _snapshotFile, true);

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

        if (!File.Exists(_snapshotFile)) return;

        var bytes = File.ReadAllBytes(_snapshotFile);

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
        if (!File.Exists(_scanRootsFile))
            return;

        var json = File.ReadAllText(_scanRootsFile);
        _scanRoots = JsonSerializer.Deserialize<Dictionary<Guid, ScanRoot>>(json) ?? new Dictionary<Guid, ScanRoot>();
    }

    private void SaveScanRoots_NoLock()
    {
        var json = JsonSerializer.Serialize(_scanRoots, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_scanRootsFile, json);
        Fsync(_scanRootsFile);
    }
    
    // Load persisted scan runs from scanruns.json (if present) and
    // overlay them on top of whatever was loaded from the snapshot.
    private void LoadScanRuns()
    {
        if (!File.Exists(_scanRunsFile))
            return; // Keep whatever ScanRuns came from the snapshot

        var json = File.ReadAllText(_scanRunsFile);
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
        File.WriteAllText(_scanRunsFile, json);
        Fsync(_scanRunsFile);
    }
    
    private void ReplayDeltas()
    {
        if (!Directory.Exists(_logDir)) return;

        var files = Directory.GetFiles(_logDir, $"{_meta.Generation}-*.delta")
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
        if (!Directory.Exists(_logDir)) return;

        foreach (var path in Directory.GetFiles(_logDir, $"{_meta.Generation}-*.delta"))
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
    
    // ---------- util ----------

    private static void Fsync(string path)
    {
        using var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        fs.Flush(true);
    }

    private (long logBytes, int count) GetLogSizeAndCount()
    {
        if (!Directory.Exists(_logDir)) return (0L, 0);

        long bytes = 0;
        var count = 0;
        foreach (var p in Directory.GetFiles(_logDir, $"{_meta.Generation}-*.delta"))
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