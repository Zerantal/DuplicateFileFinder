using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    // _meta persistence dirty/version tracking (under _sync)
    private long _metaVersion;
    private long _persistedMetaVersion;

    private void MarkMetaDirty_NoLock()
    {
        _metaVersion++;
    }

    private bool TryBuildMetaFileForPersist_NoLock(out RepoMetaFile metaFile, out long version)
    {
        if (_persistedMetaVersion == _metaVersion)
        {
            metaFile = null!;
            version = _persistedMetaVersion;
            return false;
        }

        // Ensure _metaFile mirrors the current in-memory state
        metaFile = new RepoMetaFile
        {
            Meta = _meta,
            ScanRoots = _scanRoots.Values.ToList(),
            ScanRuns = _scanRuns.ToList()
        };

        version = _metaVersion;
        return true;
    }

    private async Task PersistMetaIfDirtyAsync(CancellationToken ct = default)
    {
        RepoMetaFile metaFile;
        long versionToPersist;

        lock (_sync)
        {
            if (!TryBuildMetaFileForPersist_NoLock(out metaFile, out versionToPersist))
                return;

            // Update _metaFile snapshot we track in memory
            _metaFile = metaFile;
        }

        // Persist outside lock; RepoStore is gated
        await RepoStore.SaveMetaAsync(_repoPath, metaFile, ct).ConfigureAwait(false);

        lock (_sync)
        {
            // If we persisted the current version, advance persisted marker
            if (_persistedMetaVersion < versionToPersist)
                _persistedMetaVersion = versionToPersist;
        }
    }

    private Task PersistScanRootSnapshotV2Async(ScanRootSnapshotV2 snapshot, CancellationToken ct = default)
    {
        // single place to write V2 snapshots (RepoStore is gated + tmp unique)
        return RepoStore.SaveScanRootSnapshotV2Async(_repoPath, snapshot, ct);
    }
    
    private void LoadFromMetaFile(RepoMetaFile metaFile)
    {
        _metaFile = metaFile;

        _meta = metaFile.Meta with
        {
            // ensure schema version is current
            SchemaVersion = RepoSchemaVersion
        };

        _scanRoots.Clear();
        foreach (var root in metaFile.ScanRoots)
            _scanRoots[root.RootId] = root;

        _scanRuns.Clear();
        _scanRuns.AddRange(metaFile.ScanRuns);

        _scanRunIndex.Clear();
        foreach (var run in _scanRuns)
            _scanRunIndex[run.ScanSequence] = run;
        
        _metaVersion = 1;
        _persistedMetaVersion = 1;
    }

    private async Task InitialiseStateFromStoreAsync(CancellationToken ct)
    {
        // Load per-root snapshots
        foreach (var root in _scanRoots.Values)
        {
            ct.ThrowIfCancellationRequested();

            var snap = await RepoStore.LoadScanRootSnapshotV2Async(_repoPath, root.RootId, ct)
                .ConfigureAwait(false);

            if (snap is null) continue;
            _scanRootSnapshots[root.RootId] = snap.Value;
        }
    }
}