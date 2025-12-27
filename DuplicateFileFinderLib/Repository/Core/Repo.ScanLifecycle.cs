using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    // ReSharper disable once UnusedMember.Local
    private long AllocateRunId()
    {
        lock (_sync)
        {
            var id = AllocateRunId_NoLock();
            return id;
        }
    }

    private long AllocateRunId_NoLock()
    {
        var seq = _meta.NextScanSequence;
        _meta = _meta with { NextScanSequence = seq + 1 };
        MarkMetaDirty_NoLock();
        return seq;
    }

    public long AllocateDirId()
    {
        lock (_sync)
        {
            return AllocateDirId_NoLock();
        }
    }

    private long AllocateDirId_NoLock()
    {
        var id = _meta.NextDirId;
        _meta = _meta with { NextDirId = id + 1 };
        MarkMetaDirty_NoLock();
        return id;
    }

    public long AllocateFileId()
    {
        lock (_sync)
        {
            return AllocateFileId_NoLock();
        }
    }

    private long AllocateFileId_NoLock()
    {
        var id = _meta.NextFileId;
        _meta = _meta with { NextFileId = id + 1 };
        MarkMetaDirty_NoLock();
        return id;
    }

    private long AllocateRootId_NoLock()
    {
        var id = _meta.NextScanRootId;
        _meta = _meta with { NextScanRootId = id + 1 };
        MarkMetaDirty_NoLock();
        return id;
    }
    
    async Task IRepoInternal.MarkScanCompletedAsync(long sequence, CancellationToken ct)
    {
        ScanRun updated;
        long generation;
        
        lock (_sync)
        {
            if (!_scanRunIndex.TryGetValue(sequence, out var run))
                return;

            updated = run with
            {
                Status = ScanRunStatus.Completed,
                FinishedAt = DateTimeOffset.UtcNow,
                ErrorMessage = null
            };

            _scanRunIndex[sequence] = updated;

            var idx = _scanRuns.FindIndex(r => r.ScanSequence == sequence);
            if (idx >= 0) _scanRuns[idx] = updated;
            else _scanRuns.Add(updated);

            generation = _meta.Generation + 1;
            _meta = _meta with { Generation = generation };
            MarkMetaDirty_NoLock();
        }
    
        await PersistMetaIfDirtyAsync(ct).ConfigureAwait(false);
        PublishEvent(new ScanRunFinalisedEvent { Generation = generation, Run = updated });
    }

    async Task IRepoInternal.MarkScanFailedAsync(long sequence, string? errorMessage, bool cancelled, CancellationToken ct)
    {
        long generation;
        ScanRun updated;
        
        lock (_sync)
        {
            if (!_scanRunIndex.TryGetValue(sequence, out var run))
                return;

            var status = cancelled ? ScanRunStatus.Cancelled : ScanRunStatus.Failed;

            updated = run with
            {
                Status = status,
                FinishedAt = DateTimeOffset.UtcNow,
                ErrorMessage = errorMessage
            };

            _scanRunIndex[sequence] = updated;

            var idx = _scanRuns.FindIndex(r => r.ScanSequence == sequence);
            if (idx >= 0) _scanRuns[idx] = updated;
            else _scanRuns.Add(updated);

            generation = _meta.Generation + 1;
            _meta = _meta with { Generation = generation };
            MarkMetaDirty_NoLock();
        }

        await PersistMetaIfDirtyAsync(ct).ConfigureAwait(false);
        PublishEvent(new ScanRunFinalisedEvent { Generation = generation, Run = updated });
    }
    
    // Find existing ScanRoot by canonical path or create a new one.
    private ScanRoot FindOrCreateScanRoot_NoLock(string? volumePath, string relativeRootPath)
    {
        ScanRoot newRoot;
        
        lock (_sync)
        {
            foreach (var root in _scanRoots.Values.Where(r => !r.IsDeleted))
            {
                if (string.Equals(root.VolumePath, volumePath, StringComparison.Ordinal) &&
                    string.Equals(root.RootPath, relativeRootPath, StringComparison.Ordinal))
                {
                    return root;
                }
            }

            var now = DateTimeOffset.UtcNow;

            newRoot = new ScanRoot
            {
                RootId = AllocateRootId_NoLock(),
                VolumePath = volumePath,
                RootPath = relativeRootPath,
                DirId = 0,
                CreatedAt = now,
                LastScannedAt = now,
                VolumeId = null,
                VolumeLabel = null,
                IsRotational = null,
                FileSystemType = null,
                DevicePath = null,
                DeviceModel = null,
                IsDeleted = false,
                DeletedAtUtc = null
            };

            _scanRoots[newRoot.RootId] = newRoot;
            MarkMetaDirty_NoLock();
        }
        
        return newRoot;
    }

    // Merge VolumeInfo into an existing ScanRoot. Caller must hold _sync.
    private static ScanRoot UpdateScanRootFromVolume_NoLock(ScanRoot root, VolumeInfo volume)
    {
        // Use new values when provided; otherwise preserve existing ones.
        return root with
        {
            VolumeId       = volume.VolumeId    ?? root.VolumeId,
            VolumeLabel    = volume.Label       ?? root.VolumeLabel,
            IsRotational   = volume.IsRotational ?? root.IsRotational,
            FileSystemType = volume.FileSystemType ?? root.FileSystemType,
            DevicePath     = volume.DevicePath,
            DeviceModel    = volume.DeviceModel ?? root.DeviceModel,
            LastScannedAt  = DateTimeOffset.UtcNow
        };
    }
    
    Task IRepoInternal.CommitScanRootSnapshotV2Async(ScanRootSnapshotV2 snapshot, CancellationToken cancellationToken)
        => CommitScanRootSnapshotV2Async(snapshot, cancellationToken);

    private async Task CommitScanRootSnapshotV2Async(ScanRootSnapshotV2 snapshot, CancellationToken ct)
    {
        long generation;
        RepoSnapshotView snapshotView;

        lock (_sync)
        {
            _scanRootSnapshots[snapshot.ScanRootId] = snapshot;

            generation = _meta.Generation + 1;
            _meta = _meta with { Generation = generation };
            MarkMetaDirty_NoLock();

            // Capture a coherent view that corresponds to this in-memory state.
            snapshotView = GetRepoSnapshotView();
        }
            
        // Persist only the changed scanroot snapshot (RepoStore is gated + tmp unique)
        await PersistScanRootSnapshotV2Async(snapshot, ct).ConfigureAwait(false);
        await PersistMetaIfDirtyAsync(ct).ConfigureAwait(false);

        PublishEvent(new ScanRootSnapshotCommittedEvent
        {
            Generation = generation,
            ScanRootId = snapshot.ScanRootId,
            RepoSnapshotView = snapshotView
        });
    }

    async Task IRepoInternal.CommitCheckpoint(ScanCheckpoint checkpoint, CancellationToken ct)
    {
        await RepoStore.SaveScanCheckpointAsync(_repoPath, checkpoint, ct).ConfigureAwait(false);
    }
    
    public Task DeleteScanCheckpointAsync(long scanRootId, CancellationToken ct = default)
        => RepoStore.DeleteScanCheckpointAsync(_repoPath, scanRootId, ct);
}
