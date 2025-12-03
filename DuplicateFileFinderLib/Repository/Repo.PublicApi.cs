using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Util;
using MemoryPack;

namespace DuplicateFileFinderLib.Repository;

public sealed partial class Repo
{
    internal RepoMeta Meta { get; private set; } = null!;

    // Public read-only views
    public IReadOnlyList<ScanRoot> ScanRootsView => _scanRoots.Values.ToList();
    public IReadOnlyList<ScanRun> ScanRunsView => _scanRuns;
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        Dictionary<long, DirRecord>? dirsCopy;
        Dictionary<long, FileRecord>? filesCopy;
        RepoMetaFile? metaCopy;
        List<ScanRoot>? scanRootsCopy;

        // 1. Capture in-memory state safely
        lock (_sync)
        {
            // Take local snapshots of the in-memory dictionaries for async work outside lock
            dirsCopy = new Dictionary<long, DirRecord>(_dirs);
            filesCopy = new Dictionary<long, FileRecord>(_files);

            // Sync metaFile so it's up to date
            SyncMetaFile_NoLock();
            metaCopy = _metaFile;

            // Capture roots (for per-root flush)
            scanRootsCopy = _scanRoots.Values.ToList();
        }

        // 2. Save meta.mp
        await RepoStore.SaveMetaAsync(_repoPath, metaCopy).ConfigureAwait(false);

        // 3. Write per-root snapshots
        foreach (var root in scanRootsCopy)
            await PersistScanRootSnapshotAsync(
                root.RootId,
                dirsCopy,
                filesCopy,
                CancellationToken.None
            ).ConfigureAwait(false);

        // 4. clean deltas / compaction at shutdown
        lock (_sync)
        {
            DeleteObsoleteDeltas_NoLock();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    
    public RepoViewSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return CreateSnapshot_NoLock();
        }
    }

    private RepoViewSnapshot CreateSnapshot_NoLock()
    {
        var files = new Dictionary<long, FileRecord>(_files);
        var dirs = new Dictionary<long, DirRecord>(_dirs);

        return new RepoViewSnapshot
        {
            Files = files,
            Dirs = dirs
        };
    }
    

    public void RemoveScanRoot(string rootPath)
    {
        rootPath = PathUtils.NormalizePath(rootPath);
        var snap = GetSnapshot();

        var dirsToDelete = new List<DirRecord>();
        var filesToDelete = new List<FileRecord>();

        var seq = AllocateRunId();
        foreach (var dir in snap.Dirs.Values)
        {
            var dirPath = GetFullDirPath(dir.DirId);
            var normalized = PathUtils.NormalizePath(dirPath);
            if (normalized.StartsWith(rootPath, PathUtils.PathComparison)) dirsToDelete.Add(dir);
        }

        foreach (var file in snap.Files.Values)
        {
            var dirPath = GetFullDirPath(file.DirId);
            var full = PathUtils.NormalizePath(Path.Combine(dirPath, file.Name));
            if (full.StartsWith(rootPath, PathUtils.PathComparison)) filesToDelete.Add(file);
        }

        if (dirsToDelete.Count == 0 && filesToDelete.Count == 0)
            return;

        var delta = new RepoDelta
        {
            ScanSequence = seq,
            Dirs = dirsToDelete,
            Files = filesToDelete
        };

        CommitDelta(delta);
    }

    // -------- BeginScan (creates ScanRun + ScanSession) --------

    public IScanSession BeginScan(
        string rootPath,
        ScanMode scanMode,
        VolumeInfo? vInfo,
        int maxFilesBeforeFlush,
        int maxDirsBeforeFlush)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path is null or empty.", nameof(rootPath));

        var normalizedRootPath = PathUtils.NormalizePath(rootPath);

        ScanRun run;
        RepoDelta? rootDelta = null;
        Dictionary<long, DirRecord> existingDirs;

        lock (_sync)
        {
            var runId = AllocateRunId_NoLock();

            // Find or create the ScanRoot for this logical path
            var scanRoot = FindOrCreateScanRoot_NoLock(normalizedRootPath);

            // Ensure ScanRoot.DirId is bound to a real DirRecord in _dirs.
            if (scanRoot.DirId == 0 || !_dirs.ContainsKey(scanRoot.DirId))
            {
                // Try to reuse an existing dir whose full path matches the root path
                long? existingRootDirId = null;

                foreach (var kv in _dirs)
                {
                    var full = GetFullDirPath(kv.Key);
                    if (PathUtils.IsSamePath(full, normalizedRootPath))
                    {
                        existingRootDirId = kv.Key;
                        break;
                    }
                }

                if (existingRootDirId is { } reuseId)
                {
                    scanRoot = scanRoot with { DirId = reuseId };
                }
                else
                {
                    // No existing dir corresponds to this root – create a dummy root dir.
                    var trimmed = normalizedRootPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);

                    var name = Path.GetFileName(trimmed);
                    if (string.IsNullOrEmpty(name))
                        name = normalizedRootPath; // fallback for "/" or drive roots
                    
                    var rootDirId = AllocateDirId_NoLock();

                    var rootDir = new DirRecord
                    {
                        DirId = rootDirId,
                        ParentDirId = null,
                        Name = name,
                        LastSeenScanSequence = runId,
                        Status = ScanEntryStatus.None, // “known root, not yet enumerated”
                        ErrorMessage = null
                    };

                    _dirs[rootDirId] = rootDir;

                    scanRoot = scanRoot with { DirId = rootDirId};
                    _scanRoots[scanRoot.RootId] = scanRoot;

                    // Build a tiny delta to persist the root dir
                    rootDelta = new RepoDelta
                    {
                        ScanSequence = runId,
                        Dirs = new List<DirRecord> { rootDir }
                    };
                }
                _scanRoots[scanRoot.RootId] = scanRoot;
            }

            if (vInfo is not null) scanRoot = UpdateScanRootFromVolume_NoLock(scanRoot, vInfo);

            run = new ScanRun
            {
                ScanRootId = scanRoot.RootId,
                ScanSequence = runId,
                RootPath = normalizedRootPath,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = null,
                Status = ScanRunStatus.InProgress,
                ErrorMessage = null,
                Mode = scanMode
            };

            _scanRuns.Add(run);
            _scanRunIndex[runId] = run;
            SaveMeta_NoLock();
            
            existingDirs = _dirs.ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        if (rootDelta is not null)
            CommitDelta(rootDelta);

        return new ScanSession(this, run, existingDirs, maxFilesBeforeFlush, maxDirsBeforeFlush);
    }

    // -------- CommitDelta: progressive, with log id --------

    public void CommitDelta(RepoDelta delta)
    {
        // Simple bridge: ScanSession should use CommitDeltaAsync; other callers can stay sync.
        CommitDeltaAsync(delta).GetAwaiter().GetResult();
    }

    public async Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default)
    {
        var logId = AllocateLogId();

        var tmp = Path.Combine(_logDirPath, $"{Meta.Generation}-{logId}.tmp");
        var final = Path.Combine(_logDirPath, $"{Meta.Generation}-{logId}.delta");

        var bytes = MemoryPackSerializer.Serialize(delta);

        await File.WriteAllBytesAsync(tmp, bytes, cancellationToken).ConfigureAwait(false);
        Fsync(tmp); // TODO: Async versions of FSync?
        File.Move(tmp, final, true);

        long generation;
        long nextLogSequence;
        lock (_sync)
        {
            generation = Meta.Generation;
            nextLogSequence = Meta.NextLogSequence;
            ApplyDelta_NoLock(delta);   
        }

        OnDeltaCommitted(generation, nextLogSequence, delta);
        
    }

    private void OnDeltaCommitted(long generation, long nextLogSequence, RepoDelta delta)
    {
        var evt = new DeltaCommittedEvent
        {
            Generation      = generation,
            NextLogSequence = nextLogSequence,
            ScanSequence    = delta.ScanSequence,
            Delta           = delta
        };

        PublishEvent(evt);
    }

    public void SaveScanSnapshots()
    {
        lock (_sync)
        {
            SaveScanSnapshots_NoLock();
        }
    }

    public string GetFullDirPath(long dirId)
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

            if (cursor.ParentDirId is { } parentId)
            {
                if (!_dirs.TryGetValue(parentId, out cursor))
                    // Console.WriteLine($"parentId {parentId} not found in repo. Dir = {node}");
                    // return node.Name;
                    throw new InvalidOperationException(
                        $"Broken parent chain: missing parent {parentId}");
            }
            else
            {
                // root path from scan root
                var scanRoot = _scanRoots.Values.FirstOrDefault(r => r.DirId == cursor.DirId);
                if (scanRoot is not null)
                {
                    parts.AddRange(scanRoot.RootPath.Split('/', StringSplitOptions.RemoveEmptyEntries)[..^1].Reverse());
                }

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
    
    public async Task CompactAsync(RepoCompactionPolicy? policy = null, CancellationToken ct = default)
    {
        if (policy is not null)
        {
            if (!await ShouldCompactAsync(policy).ConfigureAwait(false))
                return; // no-op: thresholds not met
        }

        Dictionary<long, DirRecord>  dirsSnapshot;
        Dictionary<long, FileRecord> filesSnapshot;
        List<ScanRoot>               rootsSnapshot;
        long nextLogSeq;
        long generation;

        //
        // 1. Snapshot log counters under the single repo lock
        //
        lock (_sync)
        {
            // Clone into fresh dictionaries so we can operate outside the lock
            dirsSnapshot   = new Dictionary<long, DirRecord>(_dirs);
            filesSnapshot  = new Dictionary<long, FileRecord>(_files);
            rootsSnapshot  = _scanRoots.Values.ToList();

            nextLogSeq = Meta.NextLogSequence;
            generation = Meta.Generation;
        }

        //
        // 2. Pre-index files by DirId for efficient per-root selection
        //
        var filesByDir = filesSnapshot.Values
            .GroupBy(f => f.DirId)
            .ToDictionary(g => g.Key, g => g.ToList());

        //
        // 3. Write per-root snapshots based on the in-memory snapshot (outside lock)
        //
        foreach (var root in rootsSnapshot)
        {
            ct.ThrowIfCancellationRequested();

            if (root.DirId == 0 || !dirsSnapshot.ContainsKey(root.DirId))
            {
                // Unbound root: write an empty snapshot so the root still exists
                var emptySnap = new ScanRootSnapshotOnDisk
                {
                    ScanRootId = root.RootId,
                    Dirs       = Array.Empty<DirRecord>(),
                    Files      = Array.Empty<FileRecord>()
                };

                await RepoStore.SaveScanRootSnapshotAsync(_repoPath, emptySnap, ct)
                    .ConfigureAwait(false);

                continue;
            }

            // Collect the subtree of dirs under this root
            var subtree = CollectDirSubtree(root.DirId, dirsSnapshot);

            var dirRecs = subtree
                .Select(id => dirsSnapshot[id])
                .ToArray();

            var fileRecs = subtree
                .SelectMany(dirId =>
                    filesByDir.TryGetValue(dirId, out var list)
                        ? list
                        : Enumerable.Empty<FileRecord>())
                .ToArray();

            var snap = new ScanRootSnapshotOnDisk
            {
                ScanRootId = root.RootId,
                Dirs = dirRecs,
                Files = fileRecs
            };

            await RepoStore.SaveScanRootSnapshotAsync(_repoPath, snap, ct)
                .ConfigureAwait(false);
        }

        //
        // 4. Bump generation and mark all logs up to nextLogSeq - 1 as snapshotted
        //    under the lock, then persist meta
        //
        lock (_sync)
        {
            Meta = Meta with
            {
                Generation = generation + 1,
                LastSnapshottedLogSequence = nextLogSeq - 1,
                LastCompaction = DateTimeOffset.UtcNow
            };
            
            SaveMeta_NoLock();
        }

        //
        // 5. Delete old-generation delta files
        //
        var deltaFiles = Directory
            .GetFiles(_logDirPath, $"{generation}-*.delta")
            .ToList();

        foreach (var f in deltaFiles)
        {
            try
            {
                File.Delete(f);
            }
            catch
            {
                // Best-effort: ignore IO errors during cleanup
            }
        }

        OnCompacted();
    }

    private void OnCompacted()
    {
        long generation;
        long nextLogSeq;
        RepoViewSnapshot snapshot;
        
        lock (_sync)
        {
            generation = Meta.Generation;
            nextLogSeq = Meta.NextLogSequence;
            snapshot = CreateSnapshot_NoLock();
        }

        var evt = new CompactedEvent
        {
            Generation      = generation,
            NextLogSequence = nextLogSeq,
            Snapshot        = snapshot
        };

        PublishEvent(evt);
    }

    
    public static async Task<Repo> OpenAsync(string repoPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentNullException(nameof(repoPath));

        repoPath = Path.GetFullPath(repoPath);
        Directory.CreateDirectory(repoPath);

        // 1. Load or create RepoMetaFile (repo.mp)
        var metaFile = await RepoStore.LoadMetaAsync(repoPath, ct).ConfigureAwait(false);
        if (metaFile is null)
        {
            metaFile = new RepoMetaFile
            {
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
                },
                ScanRoots = new List<ScanRoot>(),
                ScanRuns = new List<ScanRun>()
            };

            await RepoStore.SaveMetaAsync(repoPath, metaFile, ct).ConfigureAwait(false);
        }

        var repo = new Repo(repoPath, metaFile);

        using (TimingLog.StartPhase("Opening Repo"))
        {
            await repo.InitialiseStateFromStoreAsync(ct).ConfigureAwait(false);

            TimingLog.Counter("files", repo._files.Count);
            TimingLog.Counter("dirs", repo._dirs.Count);
        }
        
        return repo;
    }
    
    private async Task<bool> ShouldCompactAsync(RepoCompactionPolicy policy)
    {
        if (policy is null)
            throw new ArgumentNullException(nameof(policy));

        var logDir = _logDirPath;
        var generation = Meta.Generation;

        return await Task.Run(() =>
        {
            if (!Directory.Exists(logDir))
                return false;

            // Pattern: "<generation>-*.delta"
            var pattern = $"{generation}-*.delta";
            var paths = Directory.GetFiles(logDir, pattern);

            long totalBytes = 0;

            foreach (var path in paths)
            {
                try
                {
                    var fi = new FileInfo(path);
                    totalBytes += fi.Length;
                }
                catch
                {
                    // Ignore IO errors and continue; a missing file
                    // shouldn't cause compaction to start accidentally.
                }
            }

            var deltaCount = paths.Length;

            return deltaCount >= policy.MinDeltaCount
                   && totalBytes >= policy.MinLogBytes;
        }).ConfigureAwait(false);
    }
    
}