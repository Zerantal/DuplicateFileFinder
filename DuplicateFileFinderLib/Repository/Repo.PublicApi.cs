using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Util;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository;

public sealed partial class Repo
{
    // Public read-only views
    public IReadOnlyList<ScanRoot> ScanRootsView => _scanRoots.Values.ToList();
    public IReadOnlyList<ScanRun> ScanRunsView  => _scanRuns;
    internal RepoMeta Meta => _meta;
    
    private Repo(string repoPath)
    {
        _metaFile = Path.Combine(repoPath, "meta.json");
        _snapshotFile = Path.Combine(repoPath, "snapshot.bin");
        _logDir = Path.Combine(repoPath, "log");
        _scanRunsFile = Path.Combine(repoPath, "scanruns.json");
        _scanRootsFile = Path.Combine(repoPath, "scanroots.json");

        Directory.CreateDirectory(_logDir);
    }
    
    public static Repo Open(string repoPath)
    {
        var repo = new Repo(repoPath);
        using (TimingLog.StartPhase("Opening Repo"))
        {
            repo.LoadMetaOrCreateFresh(repoPath);
            repo.LoadSnapshot();   // 1) base image
            repo.ReplayDeltas();   // 2) bring forward by deltas
            repo.LoadScanRuns();   // 3) overlay persisted ScanRuns
            repo.LoadScanRoots();  // 4) load existing roots, if any

            // 5) Perform schema migrations as needed
            repo.MigrateToLatest();

            TimingLog.Counter("files", repo._files.Count);
            TimingLog.Counter("dirs", repo._dirs.Count);
            TimingLog.Counter("HashIndex", repo._hashIndex.Count);
        }

        return repo;
    }
    
    public IReadOnlyList<DirRecord> GetChildDirs(Guid parentDirId)
    {
        lock (_sync)
        {
            // Typically, child count per dir is small; we avoid exposing the whole _dirs map.
            var result = new List<DirRecord>();

            foreach (var dir in _dirs.Values)
            {
                if (dir.ParentId == parentDirId)
                    result.Add(dir);
            }

            return result;
        }
    }
    
    public IReadOnlyList<FileRecord> GetChildFiles(Guid parentDirId)
    {
        lock (_sync)
        {
            var result = new List<FileRecord>();

            foreach (var file in _files.Values)
            {
                if (file.DirId == parentDirId)
                    result.Add(file);
            }

            return result;
        }
    }
    
    /// <summary>
    /// Returns all duplicate groups (files that share a hash, with group size >= 2).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<FileRecord>> GetDuplicateGroups()
    {
        // Strategy:
        // 1. Copy a shallow view of hash -> fileIds under lock (cheap vs full snapshot).
        // 2. Resolve fileIds to FileRecord outside lock via a local helper, still locking briefly.

        Dictionary<HashKey, List<Guid>> hashToIds;
        lock (_sync)
        {
            hashToIds = new Dictionary<HashKey, List<Guid>>(_hashIndex.Count);
            foreach (var kv in _hashIndex)
            {
                // Only consider candidates with possible duplicates
                if (kv.Value.Count < 2)
                    continue;

                hashToIds[kv.Key] = new List<Guid>(kv.Value);
            }
        }

        var groups = new List<IReadOnlyList<FileRecord>>();

        foreach (var kv in hashToIds)
        {
            var ids = kv.Value;
            if (ids.Count < 2)
                continue;

            var files = new List<FileRecord>(ids.Count);

            lock (_sync)
            {
                foreach (var id in ids)
                {
                    if (_files.TryGetValue(id, out var file))
                        files.Add(file);
                }
            }

            if (files.Count >= 2)
                groups.Add(files);
        }

        return groups;
    }
    
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
    
    public void RemoveScanRoot(string rootPath)
    {
        rootPath = PathUtils.NormalizePath(rootPath);
        var snap = GetSnapshot();

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var dirsToDelete  = new List<DirTombstone>();
        var filesToDelete = new List<FileTombstone>();

        long seq = AllocateScanSequence();
        foreach (var dir in snap.Dirs.Values)
        {
            var dirPath = GetFullDirPath(dir.Id);
            var normalized = PathUtils.NormalizePath(dirPath);
            if (normalized.StartsWith(rootPath, comparison))
            {
                dirsToDelete.Add(new DirTombstone(dir.Id, seq));
            }
        }

        foreach (var file in snap.Files.Values)
        {
            var dirPath = GetFullDirPath(file.DirId);
            var full = PathUtils.NormalizePath(Path.Combine(dirPath, file.Name));
            if (full.StartsWith(rootPath, comparison))
            {
                filesToDelete.Add(new FileTombstone(file.Id, seq));
            }
        }

        if (dirsToDelete.Count == 0 && filesToDelete.Count == 0)
            return;

        var delta = new RepoDelta
        {
            ScanSequence = seq,
            Dirs         = new List<DirRecord>(),
            Files        = new List<FileRecord>(),
            DeletedDirs  = dirsToDelete,
            DeletedFiles = filesToDelete
        };

        CommitDelta(delta);
    }
    
    public void SaveSnapshot()
    {
        lock (_sync)
        {
            SaveSnapshot_NoLock();
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
            ErrorMessage = null,
            ScanRootId = Guid.NewGuid(),
            Mode = ScanMode.Quick
        };
        IReadOnlyDictionary<Guid, DirRecord> dirsCopy;
        lock (_sync)
        {
            dirsCopy = _dirs.ToDictionary(kv => kv.Key, kv => kv.Value);
            _scanRunIndex[scanSequence] = run;
            _scanRuns.Add(run);
            SaveScanRuns_NoLock(); 
        }
        
        return new ScanSession(this, run, dirsCopy,  maxFilesBeforeFlush, maxDirsBeforeFlush);
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

        var tmp = Path.Combine(_logDir, $"{_meta.Generation}-{logId}.tmp");
        var final = Path.Combine(_logDir, $"{_meta.Generation}-{logId}.delta");

        var bytes = MemoryPackSerializer.Serialize(delta);

        await File.WriteAllBytesAsync(tmp, bytes, cancellationToken).ConfigureAwait(false);
        Fsync(tmp); // still sync; if you want fully async, you’d need an async fsync wrapper
        File.Move(tmp, final, true);

        ApplyDelta(delta);
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
            SaveSnapshot_NoLock(); // sets _meta.LastSnapshottedLogSequence = _meta.NextLogSequence and persists meta

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
    
    // public ScanRoot GetOrCreateScanRoot(string canonicalRootPath, Guid dirId)
    // {
    //     lock (_sync)
    //     {
    //         var existing = _scanRoots.FirstOrDefault(r =>
    //             string.Equals(r.RootPath, canonicalRootPath, StringComparison.Ordinal));
    //
    //         if (existing is not null)
    //             return existing;
    //
    //         var root = new ScanRoot
    //         {
    //             Id         = Guid.NewGuid(),
    //             RootPath   = canonicalRootPath,
    //             DirId      = dirId,
    //             CreatedAt  = DateTimeOffset.UtcNow
    //         };
    //         
    //         _scanRoots.Add(root);
    //         
    //         return root;
    //     }
    // }
    
    // public void UpdateScanRootVolume(ScanRoot root, VolumeInfo volume)
    // {
    //     lock (_sync)
    //     {
    //         var stored = _scanRoots.FirstOrDefault(r => r.DirId == root.Id);;
    //         if (stored == null)
    //             return;
    //
    //         _scanRoots[root.Id] = stored with
    //         {
    //             LastVolumeId = volume.VolumeId,
    //             LastVolumeDisplayName = volume.DisplayName,
    //             LastIsRotational = volume.IsRotational,
    //             LastVolumeFileSystemType = volume.FileSystemType,
    //             LastVolumeDevicePath = volume.DevicePath,
    //             LastScannedAt = DateTimeOffset.UtcNow
    //         };
    //     }
    // }
    
    // public void AddScanRun(ScanRun run)
    // {
    //     lock (_sync)
    //     {
    //         _scanRuns.Add(run);
    //         _meta = _meta with { NextScanSequence = Math.Max(_meta.NextScanSequence, run.ScanSequence + 1) };
    //     }
    // }
    
    private static bool ShouldCompact(RepoCompactionPolicy policy, long logBytes, int deltaCount, long snapBytes)
    {
        if (deltaCount < policy.MinDeltaCount) return false;
        if (logBytes < policy.MinLogBytes) return false;

        if (snapBytes <= 0) return true; // no snapshot yet → compact

        var ratio = (double)logBytes / Math.Max(1L, snapBytes);
        return ratio >= policy.RatioThreshold;
    }
}