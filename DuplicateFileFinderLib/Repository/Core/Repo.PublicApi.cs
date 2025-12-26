using System.Collections.ObjectModel;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    private RepoMeta Meta { get; set; } = null!;

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
                return _scanRuns.ToArray();
            }
        }    
    }
    

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Repo does not own plugins; RepoHost disposes plugins.
        // Repo persistence is explicit via session/plugin pathways.
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ScanRootSnapshotView? TryGetScanRootView(long scanRootId)
    {
        lock (_sync)
        {
            if (!_scanRootSnapshots.TryGetValue(scanRootId, out var rootSnapshot))
                return null;

            return new ScanRootSnapshotView
            {
                ScanRootId = rootSnapshot.ScanRootId,
                StringPool = rootSnapshot.StringPool,
                Dirs = rootSnapshot.Dirs,
                Files = rootSnapshot.Files
            };
        }
    }

    public RepoSnapshotView GetRepoSnapshotView()
    {
        lock (_sync)
        {
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
                Snapshots = new ReadOnlyDictionary<long, ScanRootSnapshotView>(snapshots),
                ScanRoots = new ReadOnlyDictionary<long, ScanRoot>(new Dictionary<long, ScanRoot>(_scanRoots))
            };
        }
    }

    public async Task DeleteScanRootAsync(long scanRootId, CancellationToken ct)
    {
        bool metaDirty;
        lock (_sync)
        {
            // 1) Remove the snapshot from live state (source of truth)
            _scanRootSnapshots.Remove(scanRootId);

            // 2) Mark ScanRoot as deleted (metadata)
            if (_scanRoots.TryGetValue(scanRootId, out var scanRoot))
            {
                scanRoot = scanRoot with
                {
                    IsDeleted = true,
                    DeletedAtUtc = DateTimeOffset.UtcNow
                };
                _scanRoots[scanRootId] = scanRoot;
                MarkMetaDirty_NoLock();
            }

            metaDirty = true;
        }

        // Persist outside lock; RepoStore is gated.
        if (metaDirty)
            await PersistMetaIfDirtyAsync(ct).ConfigureAwait(false);

        // Also delete on-disk snapshot file (best effort)
        await RepoStore.DeleteScanRootSnapshotAsync(_repoPath, scanRootId, ct).ConfigureAwait(false);
    }

    // -------- BeginScan (creates ScanRun + ScanSession) --------

    public bool HasScanCheckpoint(long scanRootId)
    {
        return RepoStore.HasScanCheckpoint(_repoPath, scanRootId);
    }

    async Task<ScanContext> IRepoInternal.BeginScanAsync(
        string rootPath,
        ScanOptions options,
        VolumeInfo? volumeInfo,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException(nameof(rootPath));

        var normalizedRootPath = PathUtils.NormalizePath(rootPath);

        string relativeRootPath;
        if (string.IsNullOrWhiteSpace(volumeInfo?.VolumePath))
            relativeRootPath = normalizedRootPath;
        else
            relativeRootPath = PathUtils.NormalizePath(
                Path.GetRelativePath(volumeInfo.VolumePath, normalizedRootPath));

        ScanRoot scanRoot;
        ScanRun run;
        DirScanInput rootDirInput;

        // ------------------------------
        // Create ScanRoot + ScanRun
        // ------------------------------
        lock (_sync)
        {
            scanRoot = FindOrCreateScanRoot_NoLock(volumeInfo?.VolumePath, relativeRootPath);

            // Ensure stable RootDirId
            if (scanRoot.DirId <= 0)
            {
                scanRoot = scanRoot with { DirId = AllocateDirId_NoLock() };
                _scanRoots[scanRoot.RootId] = scanRoot;
            }

            if (volumeInfo is not null)
                scanRoot = UpdateScanRootFromVolume_NoLock(scanRoot, volumeInfo);

            _scanRoots[scanRoot.RootId] = scanRoot;

            var runId = AllocateRunId_NoLock();

            run = new ScanRun
            {
                ScanRootId = scanRoot.RootId,
                ScanSequence = runId,
                RootPath = normalizedRootPath,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = null,
                Status = ScanRunStatus.InProgress,
                ErrorMessage = null,
                HashPolicy = options.HashPolicy
            };

            _scanRuns.Add(run);
            _scanRunIndex[runId] = run;
            MarkMetaDirty_NoLock();

            // Root directory "dummy" record (session will upsert it as Enumerated when observed)
            rootDirInput = new DirScanInput { DirId = scanRoot.DirId };
        }

        await PersistMetaIfDirtyAsync(ct).ConfigureAwait(false);

        // ------------------------------
        // Checkpoint handling (outside lock)
        // ------------------------------
        ScanCheckpoint[] checkpoints = [];

        if (options.StartFresh)
            // Explicit restart clears checkpoint
            await RepoStore.DeleteScanCheckpointAsync(_repoPath, scanRoot.RootId, ct)
                .ConfigureAwait(false);
        else
            checkpoints = await RepoStore.LoadScanCheckpointsAsync(
                _repoPath, scanRoot.RootId, ct).ConfigureAwait(false);

        // ------------------------------
        // Create session + import checkpoint
        // ------------------------------
        var session = new ScanSession(this, run, rootDirInput);

        if (checkpoints.Length != 0)
            // Sanity check + import oldest -> newest
            foreach (var cp in checkpoints)
            {
                if (cp.ScanRootId != scanRoot.RootId)
                    throw new InvalidOperationException("Checkpoint does not match ScanRoot.");

                session.ImportPartialSnapshot(cp.PartialSnapshot);
            }

        return new ScanContext
        {
            Session = session,
            ScanRoot = scanRoot,
            Run = run,
            Checkpoint = checkpoints.Length == 0 ? null : checkpoints[^1], // last checkpoint or null
            Options = options
        };
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
                    LastSnapshottedLogSequence = -1,
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
}