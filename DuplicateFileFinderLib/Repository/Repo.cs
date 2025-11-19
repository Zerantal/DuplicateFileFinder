// DuplicateFileFinderLib/Repo/Repo.cs

using System.Text.Json;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using NLog;

namespace DuplicateFileFinderLib.Repository;

/// <summary>
///     The persistent database of all scanned files across all scan locations.
///     Uses a snapshot + append-only delta log for durability.
/// </summary>
public sealed class Repo : IRepo
{
    private const int RepoSchemaVersion = 4;

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly Dictionary<Guid, string> _dirPathCache = new();
    private readonly string _logDir;

    private readonly string _metaFile;
    private readonly Dictionary<long, ScanRun> _scanRunIndex = new();
    private readonly string _snapshotFile;

    // to sync snapshot+meta mutations.
    private readonly Lock _sync = new();
    private Dictionary<Guid, DirRecord> _dirs = new();
    private Dictionary<Guid, FileRecord> _files = new();
    private Dictionary<HashKey, List<Guid>> _hashIndex = new();

    private Repo(string repoPath)
    {
        _metaFile = Path.Combine(repoPath, "meta.json");
        _snapshotFile = Path.Combine(repoPath, "snapshot.bin");
        _logDir = Path.Combine(repoPath, "log");
        Directory.CreateDirectory(_logDir);
    }

    private RepoMeta Meta { get; set; } = null!;
    
    private List<ScanRun> ScanRuns { get; set; } = new();

    public event EventHandler<RepoDelta>? DeltaCommitted;

    // ---------- public ops ----------

    public RepoViewSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            // Clone dictionaries so the caller gets its own copies.
            var filesCopy = _files.ToDictionary(kv => kv.Key, kv => kv.Value);
            var dirsCopy = _dirs.ToDictionary(kv => kv.Key, kv => kv.Value);

            var hashIndexCopy = _hashIndex.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<Guid>)kv.Value.ToArray());

            return new RepoViewSnapshot
            {
                Files = filesCopy,
                Dirs = dirsCopy,
                HashIndex = hashIndexCopy
            };
        }
    }

    // -------- BeginScan (creates ScanRun + ScanSession) --------

    public IScanSession BeginScan(
        string rootPath,
        int maxFilesBeforeFlush = 50_000,
        int maxDirsBeforeFlush = 1_000)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException(nameof(rootPath));

        var scanSequence = AllocateScanSequence();
        var run = new ScanRun
        {
            ScanSequence = scanSequence,
            RootPath = rootPath,
            StartedAt = DateTimeOffset.UtcNow,
            Status = ScanRunStatus.InProgress,
            FinishedAt = null,
            ErrorMessage = null
        };

        lock (_sync)
        {
            _scanRunIndex[scanSequence] = run;
            ScanRuns.Add(run);
        }

        return new ScanSession(this, run, maxFilesBeforeFlush, maxDirsBeforeFlush);
    }

    // -------- CommitDelta: progressive, with log id --------

    public void CommitDelta(RepoDelta delta)
    {
        // Simple bridge: ScanSession should use CommitDeltaAsync; other callers can stay sync.
        CommitDeltaAsync(delta).GetAwaiter().GetResult();
    }

    public async Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default)
    {
        // delta.ScanSequence must already be set by caller (ScanSession)
        var logId = AllocateLogId(); // still sync + locked; fine

        var tmp = Path.Combine(_logDir, $"{Meta.Generation}-{logId}.tmp");
        var final = Path.Combine(_logDir, $"{Meta.Generation}-{logId}.delta");

        var bytes = MemoryPackSerializer.Serialize(delta);

        await File.WriteAllBytesAsync(tmp, bytes, cancellationToken).ConfigureAwait(false);
        Fsync(tmp); // still sync; if you want fully async, you’d need an async fsync wrapper
        File.Move(tmp, final, true);

        ApplyDelta(delta);

        DeltaCommitted?.Invoke(this, delta);
    }

    public void SaveSnapshot()
    {
        lock (_sync)
        {
            SaveSnapshot_NoLock();
        }
    }

    // ---------- Compaction ---------

    public void CompactIfNeeded(RepoCompactionPolicy? policy = null)
    {
        policy ??= new RepoCompactionPolicy();

        // Fast path: compute sizes without locking
        var (logBytes, deltaCount) = GetLogSizeAndCount();
        var snapBytes = File.Exists(_snapshotFile) ? new FileInfo(_snapshotFile).Length : 0L;

        if (!ShouldCompact(policy, logBytes, deltaCount, snapBytes))
            return;

        // Serialize with other writers
        lock (_sync)
        {
            // Recompute under lock to avoid TOCTOU
            (logBytes, deltaCount) = GetLogSizeAndCount();
            snapBytes = File.Exists(_snapshotFile) ? new FileInfo(_snapshotFile).Length : 0L;
            if (!ShouldCompact(policy, logBytes, deltaCount, snapBytes))
                return;

            // 1) Write a fresh snapshot
            SaveSnapshot_NoLock(); // sets Meta.LastSnapshottedLogSequence = Meta.NextLogSequence and persists meta

            // 2) Delete deltas already captured in snapshot
            DeleteObsoleteDeltas_NoLock();
        }
    }

    public void CompactNow()
    {
        lock (_sync)
        {
            SaveSnapshot_NoLock();
            DeleteObsoleteDeltas_NoLock();
        }
    }

    public string GetFullDirPath(Guid dirId)
    {
        // Fast path: return cached value
        if (_dirPathCache.TryGetValue(dirId, out var cached))
            return cached;

        if (!_dirs.TryGetValue(dirId, out var node))
            throw new KeyNotFoundException($"DirId {dirId} not found in repo.");

        // Reconstruct path from leaf → root
        var parts = new List<string>(16);

        var cursor = node;
        while (true)
        {
            parts.Add(cursor.Name);

            if (cursor.ParentId is { } parentId)
            {
                if (!_dirs.TryGetValue(parentId, out cursor))
                    throw new InvalidOperationException(
                        $"Broken parent chain: missing parent {parentId}");
            }
            else
            {
                break;
            }
        }

        // Reverse so root → leaf
        parts.Reverse();

        // Build platform-correct path
        // e.g. "/" + "home/z/Work"
        string fullPath;

        if (OperatingSystem.IsWindows())
            // On Windows, first part may already be "C:" or "D:"
            fullPath = Path.Combine(parts.ToArray());
        else
            fullPath = Path.DirectorySeparatorChar + Path.Combine(parts.ToArray());

        _dirPathCache[dirId] = fullPath;
        return fullPath;
    }


    public static Repo Open(string repoPath)
    {
        var repo = new Repo(repoPath);
        using (TimingLog.StartPhase("Opening Repo"))
        {
            repo.LoadMetaOrCreateFresh(repoPath);
            repo.LoadSnapshot(); // 1) data image
            repo.ReplayDeltas(); // 2) bring forward by deltas; ApplyDelta updates HashIndex

            TimingLog.Counter("files", repo._files.Count);
            TimingLog.Counter("dirs", repo._dirs.Count);
            TimingLog.Counter("HashIndex", repo._hashIndex.Count);
        }

        return repo;
    }

    // Allocate a new scan/log sequence, persist it, and return it.
    internal long AllocateScanSequence()
    {
        lock (_sync)
        {
            var seq = Meta.NextScanSequence;
            Meta = Meta with { NextScanSequence = seq + 1 };
            SaveMeta_NoLock();
            return seq;
        }
    }

    internal long AllocateLogId()
    {
        lock (_sync)
        {
            var id = Meta.NextLogSequence;
            Meta = Meta with { NextLogSequence = id + 1 };
            SaveMeta_NoLock();
            return id;
        }
    }

    private void SaveMeta_NoLock()
    {
        var json = JsonSerializer.Serialize(Meta, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_metaFile, json);
        Fsync(_metaFile);
    }

// Writes snapshot + indexes and updates meta. Caller must hold _sync.
    private void SaveSnapshot_NoLock()
    {
        var lastSnapLog = Meta.NextLogSequence - 1; // -1 when no logs yet

        var newMeta = Meta with
        {
            SchemaVersion = RepoSchemaVersion,
            LastSnapshottedLogSequence = lastSnapLog
        };
        Meta = newMeta;

        var snapshot = new RepoSnapshot
        {
            Meta = newMeta,
            Files = _files,
            Dirs = _dirs,
            HashIndex = _hashIndex,
            ScanRuns = ScanRuns
        };

        var tmp = _snapshotFile + ".tmp";
        var bytes = MemoryPackSerializer.Serialize(snapshot);
        File.WriteAllBytes(tmp, bytes);
        Fsync(tmp);
        File.Move(tmp, _snapshotFile, true);

        SaveMeta_NoLock();
    }

    // ---------- loader pieces ----------

    private void LoadSnapshot()
    {
        _files.Clear();
        _dirs.Clear();
        _hashIndex.Clear();
        ScanRuns.Clear();
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
                // if (snapshot.Meta.RepoId != Meta.RepoId) throw ...
                // if (snapshot.Meta.Generation != Meta.Generation) throw ...

                _files = snapshot.Files;
                _dirs = snapshot.Dirs;
                _hashIndex = snapshot.HashIndex;
                ScanRuns = snapshot.ScanRuns;

                _scanRunIndex.Clear();
                foreach (var run in ScanRuns)
                    _scanRunIndex[run.ScanSequence] = run;
            }
        }
        catch (MemoryPackSerializationException)
        {
            Log.Error("Failed to load repo snapshot.");
            throw;
        }
    }

    private void ReplayDeltas()
    {
        if (!Directory.Exists(_logDir)) return;

        var files = Directory.GetFiles(_logDir, $"{Meta.Generation}-*.delta")
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<logId>"
            var dash = name.IndexOf('-');
            if (dash <= 0) continue;

            var idPart = name[(dash + 1)..];
            if (long.TryParse(idPart, out var logId))
                // skip deltas already covered by snapshot
                if (logId <= Meta.LastSnapshottedLogSequence)
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
                if (!existing.Hash.Equals(f.Hash))
                    if (_hashIndex.TryGetValue(existing.Hash, out var oldList))
                    {
                        oldList.Remove(f.Id);
                        if (oldList.Count == 0)
                            _hashIndex.Remove(existing.Hash);
                    }

            _files[f.Id] = f;

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
            _dirPathCache.Remove(d.Id);
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
                _dirPathCache.Remove(tomb.Id);
            }
    }

    private static bool ShouldCompact(RepoCompactionPolicy policy, long logBytes, int deltaCount, long snapBytes)
    {
        if (deltaCount < policy.MinDeltaCount) return false;
        if (logBytes < policy.MinLogBytes) return false;

        if (snapBytes <= 0) return true; // no snapshot yet → compact

        var ratio = (double)logBytes / Math.Max(1L, snapBytes);
        return ratio >= policy.RatioThreshold;
    }

// Deletes all deltas with seq ≤ LastSnapshottedLogSequence. Caller must hold _sync.
    private void DeleteObsoleteDeltas_NoLock()
    {
        if (!Directory.Exists(_logDir)) return;

        foreach (var path in Directory.GetFiles(_logDir, $"{Meta.Generation}-*.delta"))
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

    // ---------- meta I/O ----------

    private void LoadMetaOrCreateFresh(string repoPath)
    {
        if (!File.Exists(_metaFile))
        {
            // First time creating a repo → initialise everything
            Meta = new RepoMeta
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
        Meta = JsonSerializer.Deserialize<RepoMeta>(File.ReadAllText(_metaFile))
               ?? throw new InvalidDataException("Failed to load RepoMeta.");
    }


    // Scan lifecycle helpers

    internal void MarkScanCompleted(long sequence)
    {
        lock (_sync)
        {
            if (!_scanRunIndex.TryGetValue(sequence, out var run))
                return;

            var updated = run with
            {
                Status = ScanRunStatus.Completed,
                FinishedAt = DateTimeOffset.UtcNow,
                ErrorMessage = null
            };

            _scanRunIndex[sequence] = updated;
            var idx = ScanRuns.FindIndex(r => r.ScanSequence == sequence);
            if (idx >= 0) ScanRuns[idx] = updated;
            else ScanRuns.Add(updated);
        }
    }

    internal void MarkScanFailed(long sequence, string? errorMessage, bool cancelled)
    {
        lock (_sync)
        {
            if (!_scanRunIndex.TryGetValue(sequence, out var run))
                return;

            var status = cancelled ? ScanRunStatus.Cancelled : ScanRunStatus.Failed;

            var updated = run with
            {
                Status = status,
                FinishedAt = DateTimeOffset.UtcNow,
                ErrorMessage = errorMessage
            };

            _scanRunIndex[sequence] = updated;
            var idx = ScanRuns.FindIndex(r => r.ScanSequence == sequence);
            if (idx >= 0) ScanRuns[idx] = updated;
            else ScanRuns.Add(updated);
        }
    }

    // -------- Completion: generate tombstone delta for a root --------

    internal void CompleteScanForRoot(long scanSequence, string rootPath)
    {
        // Compute which files/dirs under root were *not* seen at scanSequence
        var deletedFiles = new List<FileTombstone>();
        var deletedDirs = new List<DirTombstone>();

        foreach (var kvp in _files)
        {
            var file = kvp.Value;
            if (!IsUnderRoot(file.DirId, rootPath))
                continue;

            if (file.LastSeenScanSequence < scanSequence)
                deletedFiles.Add(new FileTombstone(file.Id, scanSequence));
        }

        foreach (var kvp in _dirs)
        {
            var dir = kvp.Value;
            if (!IsUnderRoot(dir.Id, rootPath))
                continue;

            if (dir.LastSeenSequence < scanSequence)
                deletedDirs.Add(new DirTombstone(dir.Id, scanSequence));
        }

        if (deletedFiles.Count == 0 && deletedDirs.Count == 0)
        {
            // Nothing to tombstone, just mark completed
            MarkScanCompleted(scanSequence);
            return;
        }

        var tombstoneDelta = new RepoDelta
        {
            ScanSequence = scanSequence,
            Files = new List<FileRecord>(),
            Dirs = new List<DirRecord>(),
            DeletedFiles = deletedFiles,
            DeletedDirs = deletedDirs
        };

        CommitDelta(tombstoneDelta);
        MarkScanCompleted(scanSequence);
    }

    // Implement IsUnderRoot using your directory tree + GetFullDirPath or cached mapping
    private bool IsUnderRoot(Guid dirId, string rootPath)
    {
        var path = GetFullDirPath(dirId);
        return path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
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
        foreach (var p in Directory.GetFiles(_logDir, $"{Meta.Generation}-*.delta"))
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