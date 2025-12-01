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

    // Public read-only views
    public IReadOnlyList<ScanRoot> ScanRootsView => _scanRoots.Values.ToList();
    public IReadOnlyList<ScanRun> ScanRunsView => _scanRuns;

    /// <summary>
    ///     Returns all duplicate groups (files that share a hash, with group size >= 2).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<FileRecord>> GetDuplicateGroups()
    {
        // Strategy:
        // 1. Copy a shallow view of hash -> fileIds under lock (cheap vs full snapshot).
        // 2. Resolve fileIds to FileRecord outside lock via a local helper, still locking briefly.

        Dictionary<HashKey, List<long>> hashToIds;
        lock (_sync)
        {
            hashToIds = new Dictionary<HashKey, List<long>>(_fileHashIndex.Count);
            foreach (var kv in _fileHashIndex)
            {
                // Only consider candidates with possible duplicates
                if (kv.Value.Count < 2)
                    continue;

                hashToIds[kv.Key] = new List<long>(kv.Value);
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
                    if (_files.TryGetValue(id, out var file))
                        files.Add(file);
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
            var files = new Dictionary<long, FileRecord>(_files);
            var dirs = new Dictionary<long, DirRecord>(_dirs);
            var hashIndex = BuildHashIndex(files);

            return new RepoViewSnapshot
            {
                Files = files,
                Dirs = dirs,
                HashIndex = hashIndex
            };
        }
    }

    public void RemoveScanRoot(string rootPath)
    {
        rootPath = PathUtils.NormalizePath(rootPath);
        var snap = GetSnapshot();

        var dirsToDelete = new List<DirTombstone>();
        var filesToDelete = new List<FileTombstone>();

        var seq = AllocateRunId();
        foreach (var dir in snap.Dirs.Values)
        {
            var dirPath = GetFullDirPath(dir.DirId);
            var normalized = PathUtils.NormalizePath(dirPath);
            if (normalized.StartsWith(rootPath, PathUtils.PathComparison)) dirsToDelete.Add(new DirTombstone(dir.DirId, seq));
        }

        foreach (var file in snap.Files.Values)
        {
            var dirPath = GetFullDirPath(file.DirId);
            var full = PathUtils.NormalizePath(Path.Combine(dirPath, file.Name));
            if (full.StartsWith(rootPath, PathUtils.PathComparison)) filesToDelete.Add(new FileTombstone(file.FileId, seq));
        }

        if (dirsToDelete.Count == 0 && filesToDelete.Count == 0)
            return;

        var delta = new RepoDelta
        {
            RunId = seq,
            Dirs = new List<DirRecord>(),
            Files = new List<FileRecord>(),
            DeletedDirs = dirsToDelete,
            DeletedFiles = filesToDelete
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
                        ParentId = null,
                        Name = name,
                        SeenDuringScanRunId = runId,
                        Status = ScanEntryStatus.None, // “known root, not yet enumerated”
                        ErrorMessage = null
                    };

                    _dirs[rootDirId] = rootDir;

                    scanRoot = scanRoot with { DirId = rootDirId};
                    _scanRoots[scanRoot.RootId] = scanRoot;

                    // Build a tiny delta to persist the root dir
                    rootDelta = new RepoDelta
                    {
                        RunId = runId,
                        Dirs = new List<DirRecord> { rootDir }
                    };
                }
                _scanRoots[scanRoot.RootId] = scanRoot;
            }

            if (vInfo is not null) scanRoot = UpdateScanRootFromVolume_NoLock(scanRoot, vInfo);

            run = new ScanRun
            {
                ScanRootId = scanRoot.RootId,
                ScanRunId = runId,
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

        lock (_sync)
        {
            ApplyDelta(delta);   
        }
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

            if (cursor.ParentId is { } parentId)
            {
                if (!_dirs.TryGetValue(parentId, out cursor))
                    // Console.WriteLine($"parentId {parentId} not found in repo. Dir = {node}");
                    // return node.Name;
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
    
    public async Task CompactAsync(RepoCompactionPolicy? policy = null, CancellationToken ct = default)
    {
        if (policy is not null)
        {
            if (!await ShouldCompactAsync(policy))
                return; // no-op: thresholds not met
        }

        long nextLogSeq;
        long generation;

        //
        // 1. Snapshot log counters under the single repo lock
        //
        lock (_sync)
        {
            nextLogSeq = Meta.NextLogSequence;
            generation = Meta.Generation;
        }

        //
        // 2. Rebuild a clean dictionary of all dirs/files from deltas (outside lock)
        //
        var allDirs = new Dictionary<long, DirRecord>();
        var allFiles = new Dictionary<long, FileRecord>();

        var deltaFiles = Directory
            .GetFiles(_logDirPath, $"{generation}-*.delta")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var path in deltaFiles)
        {
            ct.ThrowIfCancellationRequested();

            var bytes = await File.ReadAllBytesAsync(path, ct);
            var delta = MemoryPackSerializer.Deserialize<RepoDelta>(bytes);
            if (delta == null)
                continue;

            // Minimal pure-function ApplyDelta variant
            ApplyDelta(delta, allDirs, allFiles);
        }

        //
        // 3. Persist per-root snapshots (outside lock)
        //
        foreach (var root in _scanRoots.Values)
        {
            ct.ThrowIfCancellationRequested();

            var subtree = CollectDirSubtree(root.DirId, allDirs);

            var dirRecs = subtree.Select(id => allDirs[id]).ToArray();
            var fileRecs = subtree
                .SelectMany(dirId => allFiles.Values.Where(f => f.DirId == dirId))
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
        // 4. Commit new meta baseline + generation under lock
        //
        lock (_sync)
        {
            Meta = Meta with
            {
                Generation = generation + 1,
                LastSnapshottedLogSequence = nextLogSeq - 1,
                LastCompaction = DateTimeOffset.UtcNow
            };

            // Sync + write new meta.mp
            SyncMetaFile_NoLock();
            SaveMeta_NoLock();
        }

        //
        // 5. Remove obsolete deltas (old generation)
        //
        foreach (var f in deltaFiles)
            try
            {
                File.Delete(f);
            }
            catch
            {
                /* ignore */
            }
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
                    NextScanRunId = 0
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
            TimingLog.Counter("HashIndex", repo._fileHashIndex.Count);
        }

        return repo;
    }

    private static IReadOnlyDictionary<HashKey, IReadOnlyList<long>> BuildHashIndex(
        IReadOnlyDictionary<long, FileRecord> files)
    {
        var result = new Dictionary<HashKey, List<long>>();

        foreach (var f in files.Values)
        {
            if (!f.Hash.IsComputed)
                continue;

            if (!result.TryGetValue(f.Hash, out var list))
            {
                list = new List<long>();
                result[f.Hash] = list;
            }

            list.Add(f.FileId);
        }

        // Convert lists to IReadOnlyList
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<long>)kv.Value);
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