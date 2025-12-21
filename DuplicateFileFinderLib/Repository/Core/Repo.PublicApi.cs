using System.Collections.ObjectModel;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;
using MemoryPack;
using FileRecord = DuplicateFileFinderLib.Repository.Storage.Models.FileRecord;
using ScanRootSnapshotV2 = DuplicateFileFinderLib.Repository.Storage.Models.ScanRootSnapshotV2;
using ScanRun = DuplicateFileFinderLib.Repository.Storage.Models.ScanRun;

namespace DuplicateFileFinderLib.Repository.Core;

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

        RepoMetaFile metaCopy;
        ScanRoot[] rootsCopy;
        Dictionary<long, ScanRootSnapshotV2> snapsCopy;

        lock (_sync)
        {
            SyncMetaFile_NoLock();
            metaCopy = _metaFile;

            rootsCopy = _scanRoots.Values.ToArray();
            snapsCopy = new Dictionary<long, ScanRootSnapshotV2>(_scanRootSnapshots);
        }

        // 1) Save meta
        await RepoStore.SaveMetaAsync(_repoPath, metaCopy).ConfigureAwait(false);
        
        // 2) Persist per-root snapshots
        foreach (var root in rootsCopy)
        {
            if (root.IsDeleted)
            {
                await RepoStore.DeleteScanRootSnapshotAsync(_repoPath, root.RootId, CancellationToken.None)
                    .ConfigureAwait(false);
                continue;
            }
        
            if (snapsCopy.TryGetValue(root.RootId, out var snapV2))
            {
                await RepoStore.SaveScanRootSnapshotV2Async(_repoPath, snapV2, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                // Ensure no stale snapshot exists
                await RepoStore.DeleteScanRootSnapshotAsync(_repoPath, root.RootId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        
        // 3) best-effort cleanup of obsolete deltas at shutdown
        lock (_sync)
        {
            DeleteObsoleteDeltas_NoLock();
        }
    }


    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Obsolete("Use TryGetScanRootView instead")]
    public IRepoView GetRepoView()
    {
        lock (_sync)
        {
            return GetRepoView_NoLock();
        }
    }

    [Obsolete("Use TryGetScanRootView instead")]
    private IRepoView GetRepoView_NoLock()
    {
        var filesCopy = new Dictionary<long, FileRecord>(_files);
        var dirsCopy = new Dictionary<long, DirRecord>(_dirs);

        return new RepoView(dirsCopy, filesCopy);
    }
    
    
    public ScanRootSnapshotView? TryGetScanRootView(long scanRootId)
    {
        if (_scanRootSnapshots.TryGetValue(scanRootId, out var rootSnapshot))
        {
            var scanRootView = new ScanRootSnapshotView
            {
                ScanRootId = rootSnapshot.ScanRootId,
                StringPool = rootSnapshot.StringPool,
                Dirs = rootSnapshot.Dirs,
                Files = rootSnapshot.Files
            };
            
            return scanRootView;
        }

        return null;
    }
    
    public RepoSnapshotView GetRepoSnapshotView()
    {
        // Copy references so the returned view is stable even if the repo changes later.
        var snapshots = new Dictionary<long, ScanRootSnapshotView>(_scanRootSnapshots.Count);
        foreach (var (id, snap) in _scanRootSnapshots)
        {
            snapshots[id] = new ScanRootSnapshotView
            {
                ScanRootId = snap.ScanRootId,
                StringPool = snap.StringPool,
                Dirs = snap.Dirs,
                Files = snap.Files  
            };
        }

        return new RepoSnapshotView
        {
            Snapshots = new ReadOnlyDictionary<long, ScanRootSnapshotView>(new Dictionary<long, ScanRootSnapshotView>(snapshots)),
            ScanRoots = new ReadOnlyDictionary<long, ScanRoot>(new Dictionary<long, ScanRoot>(_scanRoots))
        };
    }

    // -------- BeginScan (creates ScanRun + ScanSession) --------



    public IScanSession BeginScan(
        string rootPath,
        ScanOperation scanOperation,
        VolumeInfo? volumeInfo,
        int maxFilesBeforeFlush,
        int maxDirsBeforeFlush)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path is null or empty.", nameof(rootPath));

        var normalizedRootPath = PathUtils.NormalizePath(rootPath);
        string relativeRootPath;
        if (string.IsNullOrWhiteSpace(volumeInfo?.VolumePath))
            relativeRootPath = normalizedRootPath;
        else
            relativeRootPath = PathUtils.NormalizePath(Path.GetRelativePath(volumeInfo.VolumePath, normalizedRootPath));
        
        ScanRun run;
        DirScanInput rootDirInput;

        lock (_sync)
        {
            var runId = AllocateRunId_NoLock();
            
            var scanRoot = FindOrCreateScanRoot_NoLock(volumeInfo?.VolumePath, relativeRootPath);

            // Ensure there is a root dir id for this scan root (V2 model uses snapshots as truth,
            // but ScanRoot still needs a stable dirId for identity).
            if (scanRoot.DirId == 0)
            {
                var rootDirId = AllocateDirId_NoLock();
                scanRoot = scanRoot with { DirId = rootDirId};
            }
            
            if (volumeInfo is not null)
                scanRoot = UpdateScanRootFromVolume_NoLock(scanRoot, volumeInfo);

            _scanRoots[scanRoot.RootId] = scanRoot;
            
            run = new ScanRun
            {
                ScanRootId = scanRoot.RootId,
                ScanSequence = runId,
                RootPath = normalizedRootPath,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = null,
                Status = ScanRunStatus.InProgress,
                ErrorMessage = null,
                Operation = scanOperation
            };

            _scanRuns.Add(run);
            _scanRunIndex[runId] = run;

            SaveMeta_NoLock();
            
            // Root directory "dummy" record (session will upsert it as Enumerated when observed)
            rootDirInput = new DirScanInput { DirId = scanRoot.DirId };
        }

        return new ScanSession(this, run, rootDirInput, maxFilesBeforeFlush, maxDirsBeforeFlush);
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
        Repo.Fsync(tmp); // TODO: Async versions of FSync?
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
// ***************** TEMP
    private void RebuildDirHandleMap_NoLock()
    {
        _dirHandleById.Clear();

        foreach (var (scanRootId, snap) in _scanRootSnapshots)
        {
            var dirs = snap.Dirs;
            for (int i = 0; i < dirs.Length; i++)
            {
                var dirId = dirs[i].DirId;

                // Fail fast on duplicate ids – your design assumes global uniqueness.
                if (!_dirHandleById.TryAdd(dirId, new DirHandle(scanRootId, i)))
                    throw new InvalidOperationException($"Duplicate dirId {dirId} across snapshots.");
            }
        }
    }
// ***************** TEMP

    public string GetDirPathV2ByHandle(DirHandle dirHandle, bool relativeToVolumePath = false)
    {
        return GetDirPathV2(_scanRootSnapshots[dirHandle.ScanRootId].Dirs[dirHandle.Index].DirId, relativeToVolumePath);
    }

    public string GetDirPathV2(long dirId, bool relativeToVolumePath = false)
    {
        // Cache by dirId for transition
        if (_dirPathCache.TryGetValue(dirId, out var cached))
            return cached;

        DirHandle handle;
        ScanRootSnapshotV2 snap;

        lock (_sync)
        {
            // Ensure resolver exists (or rebuild lazily)
            if (_dirHandleById.Count == 0 && _scanRootSnapshots.Count != 0)
                RebuildDirHandleMap_NoLock();

            if (!_dirHandleById.TryGetValue(dirId, out handle))
                throw new KeyNotFoundException($"dirId {dirId} not found in V2 snapshots.");

            if (!_scanRootSnapshots.TryGetValue(handle.ScanRootId, out snap!))
                throw new KeyNotFoundException($"ScanRootId {handle.ScanRootId} not found in repo snapshots.");
        }

        // Reconstruct leaf → root within the scan root
        var parts = new List<string>(16);
        var cursor = handle;

        while (true)
        {
            var rec = snap.Dirs[cursor.Index];

            if (rec.NameStrIdx >= 0)
            {
                var name = snap.StringPool.GetString(rec.NameStrIdx);
                if (!string.IsNullOrEmpty(name))
                    parts.Add(name);
            }

            if (rec.ParentDirId >= 0)
            {
                // Parent lookup without plugins: use resolver map
                DirHandle parent;
                lock (_sync)
                {
                    if (!_dirHandleById.TryGetValue(rec.ParentDirId, out parent))
                        throw new InvalidOperationException($"Broken parent chain: missing parent {rec.ParentDirId} for dir {rec.DirId}.");

                    if (parent.ScanRootId != handle.ScanRootId)
                        throw new InvalidOperationException($"Broken parent chain: parent {rec.ParentDirId} resolves to a different scan root.");
                }

                cursor = parent;
                continue;
            }

            // At scan root dir: prepend scan root RootPath / VolumePath unless relativeToVolumePath
            if (!relativeToVolumePath)
            {
                ScanRoot? sr;
                lock (_sync)
                {
                    _scanRoots.TryGetValue(handle.ScanRootId, out sr);
                }

                if (sr is not null)
                {
                    AddPathSegmentsReversed(parts, sr.RootPath);
                    if (sr.VolumePath is not null)
                        AddPathSegmentsReversed(parts, sr.VolumePath);
                }
            }

            break;
        }

        parts.Reverse();

        string fullPath;
        if (OperatingSystem.IsWindows())
            fullPath = Path.Combine(parts.ToArray());
        else
            fullPath = Path.DirectorySeparatorChar + Path.Combine(parts.ToArray());

        lock (_sync)
        {
            _dirPathCache[dirId] = fullPath;
        }

        return fullPath;
    }

    private static void AddPathSegmentsReversed(List<string> parts, string path)
    {
        var segs = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = segs.Length - 1; i >= 0; i--)
            parts.Add(segs[i]);
    }

    
    [Obsolete]
    public string GetDirPath(long dirId, bool relativeToVolumePath = false)
    {
        // Fast path: return cached value
        if (_dirPathCache.TryGetValue(dirId, out var cached))
            return cached;
        
        if (!_dirs.TryGetValue(dirId, out var node))
            throw new KeyNotFoundException($"dirId {dirId} not found in repo.");

        // Reconstruct path from leaf → root
        var parts = new List<string>(16);

        var cursor = node; 
        while (true)
        {
            if (!string.IsNullOrEmpty(cursor.Name))
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
                if (relativeToVolumePath)
                    break;
                // root path from scan root
                var scanRoot = _scanRoots.Values.FirstOrDefault(r => r.DirId == cursor.DirId);
                if (scanRoot is not null)
                {
                    parts.AddRange(scanRoot.RootPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
                    if (scanRoot.VolumePath is not null)
                        parts.AddRange(scanRoot.VolumePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
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
        
        Dictionary<long, ScanRootSnapshotV2> snapshots;
        List<ScanRoot> rootsSnapshot;
        long nextLogSeq;
        long generation;

        //
        // 1. Snapshot log counters under the single repo lock
        //
        lock (_sync)
        {
            // Clone into fresh dictionaries so we can operate outside the lock
            rootsSnapshot  = _scanRoots.Values.ToList();
            snapshots = new Dictionary<long, ScanRootSnapshotV2>(_scanRootSnapshots);
            
            nextLogSeq = Meta.NextLogSequence;
            generation = Meta.Generation;
        }

        // 2. Persist all snapshots to disk, remove snapshots for deleted ScanRoots
        foreach (var root in rootsSnapshot)
        {
            ct.ThrowIfCancellationRequested();

            if (root.IsDeleted)
            {
                await RepoStore.DeleteScanRootSnapshotAsync(_repoPath, root.RootId, ct).ConfigureAwait(false);
                continue;
            }

            if (snapshots.TryGetValue(root.RootId, out var snapV2))
            {
                await RepoStore.SaveScanRootSnapshotV2Async(_repoPath, snapV2, ct).ConfigureAwait(false);
            }
            else
            {
                await RepoStore.DeleteScanRootSnapshotAsync(_repoPath, root.RootId, ct).ConfigureAwait(false);
            }
        }
        
        // 3. Bump generation and mark all logs up to nextLogSeq - 1 as snapshotted
        // under the lock, then persist meta
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
        
        // 4. Delete old-generation delta files
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
        RepoSnapshotView snapshots;
        
        lock (_sync)
        {
            generation = Meta.Generation;
            nextLogSeq = Meta.NextLogSequence;
            snapshots = GetRepoSnapshotView();
        }

        var evt = new CompactedEvent
        {
            Generation      = generation,
            NextLogSequence = nextLogSeq,
            RepoSnapshotView       = snapshots
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