// DuplicateFileFinderLib/Repository/Core/Repo.PublicApi.cs

using System.Collections.ObjectModel;

using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Helpers;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    public long Generation
    {
        get
        {
            lock (_sync)
            {
                return _meta.Generation;
            }
        }
    }

    // Public read-only views
    public IReadOnlyList<ScanRoot> ScanRootsView
    {
        get
        {
            IReadOnlyList<ScanRoot> roots;
            lock (_sync)
            {
                roots = _scanRoots.Values.ToList();
            }

            return roots;
        }
    }

    public IReadOnlyList<ScanRun> ScanRunsView
    {
        get
        {
            lock (_sync)
            {
                return _scanRunIndex.Values
                    .OrderBy(r => r.ScanSequence)
                    .ToArray();
            }
        }
    }


    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Repo does not own plugins; RepoHost disposes plugins.
        // Repo persistence is explicit via session/plugin pathways.
        await Task.CompletedTask;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ScanRootSnapshotView? TryGetScanRootView(ScanRootId scanRootId)
    {
        lock (_sync)
        {
            return !_scanRootSnapshots.TryGetValue(scanRootId, out var snap)
                ? null
                : new ScanRootSnapshotView
                {
                    ScanRootId = snap.ScanRootId,
                    StringPool = snap.StringPool,
                    Dirs = snap.Dirs,
                    Files = snap.Files
                };
        }
    }

    public RepoSnapshotView GetRepoSnapshotView()
    {
        lock (_sync)
        {
            return GetRepoSnapshotView_NoLock();
        }
    }

    private RepoSnapshotView GetRepoSnapshotView_NoLock()
        => new()
        {
            // Non-copying: the underlying dictionaries are copy-on-write, so views remain safe.
            Snapshots = new ProjectedReadOnlyDictionary<ScanRootId, ScanRootSnapshotV2, ScanRootSnapshotView>(
                _scanRootSnapshots,
                static snap => ToView(snap)),
            ScanRoots = new ReadOnlyDictionary<ScanRootId, ScanRoot>(_scanRoots)
        };


    public async Task<long> DeleteScanRootAsync(ScanRootId scanRootId, CancellationToken ct)
    {
        long generation;

        lock (_sync)
        {
            // 1) Remove the snapshot from live state (source of truth)
            RemoveScanRootSnapshot_NoLock(scanRootId);

            // 2) Mark ScanRoot as deleted (metadata)
            if (_scanRoots.TryGetValue(scanRootId, out var scanRoot))
            {
                var updated = scanRoot with { IsDeleted = true, DeletedAtUtc = DateTimeOffset.UtcNow };

                UpsertScanRoot_NoLock(updated);
            }

            // 3) Bump generation and capture a coherent view so index plugins can rebuild
            generation = _meta.Generation + 1;
            _meta = _meta with { Generation = generation };
            MarkMetaDirty_NoLock();
        }

        // Persist outside lock; RepoStore is gated.
        await PersistMetaIfDirtyAsync(ct).ConfigureAwait(false);

        // Removing a scan root should also remove any resumable scan checkpoints for it.
        await RepoStore.DeleteScanCheckpointAsync(_repoPath, scanRootId, ct).ConfigureAwait(false);

        // NOTE: We intentionally do NOT delete the on-disk scanroot snapshot file here.
        // A later prune/compaction operation can reclaim these files.

        PublishEvent(new RepoScanRootRemovedEvent { Generation = generation, ScanRootId = scanRootId });

        return generation;
    }

    public async Task SetScanRootDisplayNameAsync(ScanRootId scanRootId, string? displayName,
        CancellationToken ct = default)
    {
        long generation;
        ScanRoot? updatedScanRoot;

        // Normalise: treat blank as null
        if (displayName is not null)
        {
            displayName = displayName.Trim();
            if (displayName.Length == 0)
                displayName = null;
        }

        lock (_sync)
        {
            if (!TryUpdateScanRoot_NoLock(
                    scanRootId,
                    sr => sr with { DisplayName = displayName },
                    out updatedScanRoot) || updatedScanRoot is null)
                return;

            generation = _meta.Generation; // don't bump generation
            MarkMetaDirty_NoLock();
        }

        await PersistMetaIfDirtyAsync(ct).ConfigureAwait(false);

        PublishEvent(new ScanRootMetaChangedEvent { Generation = generation, UpdatedScanRoot = updatedScanRoot });
    }

    // -------- Scan bootstrap (creates ScanRun + ScanSession) --------

    public bool HasScanCheckpoint(ScanRootId scanRootId) => RepoStore.HasScanCheckpoint(_repoPath, scanRootId);

    /// <summary>
    /// Folder-rescan support: Begin a scan for an existing scan root, and seed the session's mutation buffer
    /// with the currently loaded snapshot so that unscanned parts of the tree remain present in the final snapshot.
    ///
    /// Requirements:
    /// - options.StartFresh must be true (folder rescans force StartFresh; caller should warn user about checkpoint deletion)
    /// - a snapshot must already be loaded in memory for this scan root (UI should only expose the action then)
    /// </summary>
    async Task<ScanContext> IRepoInternal.BeginSubtreeScanAsync(
        ScanRootId scanRootId,
        ScanOptions options,
        VolumeInfo? volumeInfo,
        CancellationToken ct)
    {
        // TODO: I think we can set options.StartFresh to true and have BeginRescanAsync delete
        // any existing checkpoints. This will mean we can forgo two checks below.
        if (!options.StartFresh)
            throw new InvalidOperationException("Subtree scans must be started fresh (StartFresh=true).");

        // Begin rescan (this will delete checkpoints due to StartFresh=true).
        var ctx = await ((IRepoInternal)this).BeginRescanAsync(scanRootId, options, volumeInfo, ct)
            .ConfigureAwait(false);

        // Subtree scan must not be resumed from a checkpoint.
        if (ctx.Checkpoint is not null)
            throw new InvalidOperationException("Subtree scan cannot resume from an existing checkpoint.");

        // Seed from currently loaded snapshot (must exist for subtree scan).
        ScanRootSnapshotV2 baseline;
        lock (_sync)
        {
            if (!_scanRootSnapshots.TryGetValue(scanRootId, out baseline))
                throw new InvalidOperationException(
                    "Cannot rescan a folder unless a snapshot is loaded for the scan root.");
        }

        // Import baseline snapshot into the session so final snapshot retains everything outside the scanned subtree.
        if (ctx.Session is ScanSession session)
            session.ImportPartialSnapshot(in baseline);
        else
            throw new InvalidOperationException($"Unexpected session type: {ctx.Session.GetType().FullName}");

        return ctx;
    }

    async Task<ScanContext> IRepoInternal.BeginNewScanAsync(
        string rootPath,
        ScanOptions options,
        VolumeInfo? volumeInfo,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException(nameof(rootPath));

        var normalizedRootPath = PathUtils.NormalizePath(rootPath);

        // Determine relative root path (stored in ScanRoot.RootPath when VolumePath is known).
        var relativeRootPath = string.IsNullOrWhiteSpace(volumeInfo?.VolumePath)
            ? normalizedRootPath
            : PathUtils.NormalizePath(Path.GetRelativePath(volumeInfo.VolumePath, normalizedRootPath));

        ScanRoot scanRoot;
        ScanRun run;
        DirScanInput rootDirInput;

        lock (_sync)
        {
            scanRoot = FindOrCreateScanRoot_NoLock(volumeInfo, relativeRootPath);
            EnsureScanRootDirId_NoLock(ref scanRoot);

            var runId = AllocateRunId_NoLock();
            run = CreateInProgressRun_NoLock(scanRoot.RootId, runId, normalizedRootPath, options);

            rootDirInput = new DirScanInput { DirId = scanRoot.DirId };
        }

        return await BeginScanCoreAsync(scanRoot, run, rootDirInput, options, ct).ConfigureAwait(false);
    }

    async Task<ScanContext> IRepoInternal.BeginRescanAsync(
        ScanRootId scanRootId,
        ScanOptions options,
        VolumeInfo? volumeInfo,
        CancellationToken ct)
    {
        ScanRoot scanRoot;
        ScanRun run;
        DirScanInput rootDirInput;

        lock (_sync)
        {
            if (!_scanRoots.TryGetValue(scanRootId, out scanRoot!))
                throw new KeyNotFoundException($"ScanRootId not found: {scanRootId}");

            // Resolve to an absolute path for this scan run.
            var resolvedRootPath = ResolveScanRootPath_NoLock(scanRoot);

            ValidateVolumeIdentity_NoLock(scanRootId, scanRoot, volumeInfo);

            EnsureScanRootDirId_NoLock(ref scanRoot);

            // Best-effort metadata refresh (no need for volumeInfo, but apply it when present).
            scanRoot = UpdateScanRootMeta_NoLock(scanRoot, volumeInfo);

            var runId = AllocateRunId_NoLock();
            run = CreateInProgressRun_NoLock(scanRoot.RootId, runId, resolvedRootPath, options);

            rootDirInput = new DirScanInput { DirId = scanRoot.DirId };
        }

        return await BeginScanCoreAsync(scanRoot, run, rootDirInput, options, ct).ConfigureAwait(false);
    }

    private async Task<ScanContext> BeginScanCoreAsync(
        ScanRoot scanRoot,
        ScanRun run,
        DirScanInput rootDirInput,
        ScanOptions options,
        CancellationToken ct)
    {
        // Persist meta outside lock (scans show up in ScanRunsView quickly).
        await PersistMetaIfDirtyAsync(ct).ConfigureAwait(false);

        // Checkpoint handling (outside lock)
        ScanCheckpoint[] checkpoints;
        if (options.StartFresh)
        {
            await RepoStore.DeleteScanCheckpointAsync(_repoPath, scanRoot.RootId, ct).ConfigureAwait(false);
            checkpoints = [];
        }
        else
        {
            checkpoints = await RepoStore.LoadScanCheckpointsAsync(_repoPath, scanRoot.RootId, ct)
                .ConfigureAwait(false);
        }

        // Create session + import checkpoint(s)
        var session = new ScanSession(this, run, rootDirInput);

        if (checkpoints.Length != 0)
        {
            // Import oldest -> newest
            foreach (var cp in checkpoints)
            {
                if (cp.ScanRootId != scanRoot.RootId)
                    throw new InvalidOperationException("Checkpoint does not match ScanRoot.");

                session.ImportPartialSnapshot(cp.PartialSnapshot);
            }
        }

        return new ScanContext
        {
            Session = session,
            ScanRoot = scanRoot,
            Run = run,
            Checkpoint = checkpoints.Length == 0 ? null : checkpoints[^1],
            Options = options
        };
    }

    private static void ValidateVolumeIdentity_NoLock(long scanRootId, ScanRoot scanRoot, VolumeInfo? volumeInfo)
    {
        // If we can probe a volume id and the scan root already has one, validate it.
        if (!string.IsNullOrWhiteSpace(scanRoot.VolumeId) &&
            !string.IsNullOrWhiteSpace(volumeInfo?.VolumeId) &&
            !string.Equals(scanRoot.VolumeId, volumeInfo.VolumeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Mounted volume does not match ScanRootId {scanRootId}. Expected VolumeId '{scanRoot.VolumeId}', got '{volumeInfo.VolumeId}'.");
        }
    }

    private void EnsureScanRootDirId_NoLock(ref ScanRoot scanRoot)
    {
        if (scanRoot.DirId > 0)
            return;

        var updated = scanRoot with { DirId = AllocateDirId_NoLock() };
        scanRoot = updated;

        UpsertScanRoot_NoLock(updated);
        MarkMetaDirty_NoLock();
    }

    private ScanRoot UpdateScanRootMeta_NoLock(ScanRoot scanRoot, VolumeInfo? volumeInfo)
    {
        var now = DateTimeOffset.UtcNow;

        var updated = scanRoot with
        {
            IsDeleted = false,
            DeletedAtUtc = null,
            LastScannedAt = now,
            VolumeId = volumeInfo?.VolumeId ?? scanRoot.VolumeId,
            VolumePath = volumeInfo?.VolumePath ?? scanRoot.VolumePath,
            VolumeLabel = volumeInfo?.Label ?? scanRoot.VolumeLabel,
            IsRotational = volumeInfo?.IsRotational ?? scanRoot.IsRotational,
            FileSystemType = volumeInfo?.FileSystemType ?? scanRoot.FileSystemType,
            DevicePath = volumeInfo?.DevicePath ?? scanRoot.DevicePath,
            DeviceModel = volumeInfo?.DeviceModel ?? scanRoot.DeviceModel
        };

        if (Equals(updated, scanRoot)) return updated;

        UpsertScanRoot_NoLock(updated);
        MarkMetaDirty_NoLock();

        return updated;
    }

    private ScanRun CreateInProgressRun_NoLock(ScanRootId scanRootId, long runId, string rootPath, ScanOptions options)
    {
        var now = DateTimeOffset.UtcNow;

        var run = new ScanRun
        {
            ScanRootId = scanRootId,
            ScanSequence = runId,
            RootPath = rootPath,
            StartedAt = now,
            FinishedAt = null,
            Status = ScanRunStatus.InProgress,
            ErrorMessage = null,
            HashPolicy = options.HashPolicy
        };

        AddScanRun_NoLock(run);
        MarkMetaDirty_NoLock();
        return run;
    }

    private static string ResolveScanRootPath_NoLock(ScanRoot scanRoot)
    {
        var rootPath = scanRoot.RootPath;

        if (!string.IsNullOrWhiteSpace(scanRoot.VolumePath) && !Path.IsPathRooted(rootPath))
            rootPath = Path.Combine(scanRoot.VolumePath!, rootPath);

        return PathUtils.NormalizePath(rootPath);
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
        }

        return repo;
    }

    public async Task<DeleteResult> DeleteFileAsync(FileHandle file, CancellationToken ct = default)
    {
        s_log.Info($"Deleting file from repo. ScanRootId: {file.ScanRootId}, FileIndex: {file.Index}");

        if (!file.IsValid)
            return DeleteResult.Fail(_meta.Generation, file.ScanRootId, "Invalid file handle.");

        // Snapshot copy-out under lock (avoid mutating in-place)
        ScanRootSnapshotV2 snap;
        lock (_sync)
        {
            if (!_scanRootSnapshots.TryGetValue(file.ScanRootId, out snap))
                return DeleteResult.Fail(_meta.Generation, file.ScanRootId, "Scan root snapshot not loaded.");

            if ((uint)file.Index >= (uint)snap.Files.Length)
                return DeleteResult.Fail(_meta.Generation, file.ScanRootId, "File handle index out of range.");
        }

        // Idempotent: already deleted/none => succeed, no-op
        var existing = snap.Files[file.Index];
        if (existing.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
            return DeleteResult.Ok(_meta.Generation, file.ScanRootId, deletedFiles: 0, deletedDirs: 0);

        // Clone files array and mark deleted
        var newFiles = (FileRecordV2[])snap.Files.Clone();
        newFiles[file.Index] = existing with { Status = ScanEntryStatus.Deleted };

        var updated = snap with { Files = newFiles };

        // Commit updated snapshot and publish a full snapshot-replaced event so
        // index plugins rebuild and RepoHost raises IndexesRebuilt.
        var (gen, snapshotView) = await CommitSnapshot_NoEventAsync(updated, ct).ConfigureAwait(false);

        PublishEvent(new ScanRootSnapshotReplacedEvent
        {
            Generation = gen,
            ScanRootId = updated.ScanRootId,
            RepoSnapshotView = snapshotView,
            Reason = RepoSnapshotCommitReason.Mutation
        });

        // Optional secondary notification for any lightweight UI listeners.
        PublishEvent(new RepoFileDeletedEvent { Generation = gen, File = file });

        return DeleteResult.Ok(gen, file.ScanRootId, deletedFiles: 1, deletedDirs: 0);
    }

    public async Task<DeleteResult> DeleteDirAsync(DirHandle dir, CancellationToken ct = default)
    {
        if (!dir.IsValid)
            return DeleteResult.Fail(_meta.Generation, dir.ScanRootId, "Invalid dir handle.");

        ScanRootSnapshotV2 snap;
        lock (_sync)
        {
            if (!_scanRootSnapshots.TryGetValue(dir.ScanRootId, out snap))
                return DeleteResult.Fail(_meta.Generation, dir.ScanRootId, "Scan root snapshot not loaded.");

            if ((uint)dir.Index >= (uint)snap.Dirs.Length)
                return DeleteResult.Fail(_meta.Generation, dir.ScanRootId, "Dir handle index out of range.");
        }

        var rootRec = snap.Dirs[dir.Index];
        if (rootRec.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
            return DeleteResult.Ok(_meta.Generation, dir.ScanRootId, deletedFiles: 0, deletedDirs: 0);

        // Compute subtree dirIds (including root)
        var subtreeDirIds = CollectDirSubtreeIds(snap.Dirs, rootRec.DirId, ct);

        // Clone arrays and mark deleted
        var newDirs = (DirRecordV2[])snap.Dirs.Clone();
        var newFiles = (FileRecordV2[])snap.Files.Clone();

        int deletedDirs = 0;
        int deletedFiles = 0;

        // Mark dirs
        for (int i = 0; i < newDirs.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var d = newDirs[i];
            if (d.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                continue;

            if (!subtreeDirIds.Contains(d.DirId))
                continue;

            newDirs[i] = d with { Status = ScanEntryStatus.Deleted };
            deletedDirs++;
        }

        // Mark files whose DirId is in subtree
        for (int i = 0; i < newFiles.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var f = newFiles[i];
            if (f.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                continue;

            if (!subtreeDirIds.Contains(f.DirId))
                continue;

            newFiles[i] = f with { Status = ScanEntryStatus.Deleted };
            deletedFiles++;
        }

        var updated = snap with { Dirs = newDirs, Files = newFiles };

        var (gen, snapshotView) = await CommitSnapshot_NoEventAsync(updated, ct).ConfigureAwait(false);

        PublishEvent(new ScanRootSnapshotReplacedEvent
        {
            Generation = gen,
            ScanRootId = updated.ScanRootId,
            RepoSnapshotView = snapshotView,
            Reason = RepoSnapshotCommitReason.Mutation
        });

        PublishEvent(new RepoDirDeletedEvent
        {
            Generation = gen,
            Dir = dir,
            DeletedDirs = deletedDirs,
            DeletedFiles = deletedFiles
        });

        return DeleteResult.Ok(gen, dir.ScanRootId, deletedFiles, deletedDirs);
    }

    private static HashSet<long> CollectDirSubtreeIds(DirRecordV2[] dirs, long rootDirId, CancellationToken ct)
    {
        // Build parentDirId -> children dirIds adjacency from live dirs
        var children = new Dictionary<long, List<long>>(capacity: Math.Max(16, dirs.Length / 4));

        for (int i = 0; i < dirs.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var d = dirs[i];
            if (d.Status is ScanEntryStatus.Deleted or ScanEntryStatus.None)
                continue;

            // ParentDirId == -1 means root sentinel
            if (d.ParentDirId < 0)
                continue;

            if (!children.TryGetValue(d.ParentDirId, out var list))
            {
                list = new List<long>(4);
                children[d.ParentDirId] = list;
            }

            list.Add(d.DirId);
        }

        // BFS
        var visited = new HashSet<long>();
        var q = new Queue<long>();

        visited.Add(rootDirId);
        q.Enqueue(rootDirId);

        while (q.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var cur = q.Dequeue();
            if (!children.TryGetValue(cur, out var kids))
                continue;

            for (int i = 0; i < kids.Count; i++)
            {
                var child = kids[i];
                if (visited.Add(child))
                    q.Enqueue(child);
            }
        }

        return visited;
    }
}
