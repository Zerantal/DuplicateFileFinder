using System.Diagnostics.CodeAnalysis;

using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;

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

    [SuppressMessage("ReSharper", "InconsistentlySynchronizedField")]
    private long AllocateRootId_NoLock()
    {
        var id = _meta.NextScanRootId;
        _meta = _meta with { NextScanRootId = id + 1 };
        MarkMetaDirty_NoLock();
        return id;
    }

    async Task IRepoInternal.MarkScanCompletedAsync(long sequence, CancellationToken ct)
    {
        ScanRun? updated;
        long generation;

        lock (_sync)
        {
            if (!TryUpdateScanRun_NoLock(sequence, run => run with
            {
                Status = ScanRunStatus.Completed,
                FinishedAt = DateTimeOffset.UtcNow,
                ErrorMessage = null
            }, out updated) || updated is null)
            {
                return;
            }

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
        ScanRun? updated;

        lock (_sync)
        {
            var status = cancelled ? ScanRunStatus.Cancelled : ScanRunStatus.Failed;

            if (!TryUpdateScanRun_NoLock(sequence, run => run with
            {
                Status = status,
                FinishedAt = DateTimeOffset.UtcNow,
                ErrorMessage = errorMessage
            }, out updated) || updated is null)
            {
                return;
            }

            generation = _meta.Generation + 1;
            _meta = _meta with { Generation = generation };
            MarkMetaDirty_NoLock();
        }

        await PersistMetaIfDirtyAsync(ct).ConfigureAwait(false);
        PublishEvent(new ScanRunFinalisedEvent { Generation = generation, Run = updated });
    }

    // Find existing ScanRoot by canonical path or create a new one.
    private ScanRoot FindOrCreateScanRoot_NoLock(VolumeInfo? volume, string relativeRootPath)
    {
        relativeRootPath = PathUtils.NormalizePath(relativeRootPath);

        string? volumeId = volume?.VolumeId;
        string? volumePath = volume?.VolumePath; // or however you store it

        ScanRoot? existing = null;

        if (!string.IsNullOrEmpty(volumeId))
        {
            existing = _scanRoots.Values.FirstOrDefault(r =>
                string.Equals(r.VolumeId, volumeId, StringComparison.Ordinal) &&
                string.Equals(r.RootPath, relativeRootPath, StringComparison.Ordinal));
        }

        // fallback to matching by volumePath instead of volumeId
        existing ??= _scanRoots.Values.FirstOrDefault(r =>
            string.Equals(r.VolumePath, volumePath, StringComparison.Ordinal) &&
            string.Equals(r.RootPath, relativeRootPath, StringComparison.Ordinal));

        var now = DateTimeOffset.UtcNow;

        if (existing is not null)
        {
            var updated = existing with
            {
                IsDeleted = false,
                DeletedAtUtc = null,
                LastScannedAt = now,

                VolumeId = volumeId ?? existing.VolumeId,
                VolumePath = volumePath ?? existing.VolumePath,
                VolumeLabel = volume?.Label ?? existing.VolumeLabel,
                IsRotational = volume?.IsRotational ?? existing.IsRotational,
                FileSystemType = volume?.FileSystemType ?? existing.FileSystemType,
                DevicePath = volume?.DevicePath ?? existing.DevicePath,
                DeviceModel = volume?.DeviceModel ?? existing.DeviceModel
            };

            UpsertScanRoot_NoLock(updated);

            MarkMetaDirty_NoLock();
            return updated;
        }

        var created = new ScanRoot
        {
            RootId = AllocateRootId_NoLock(),
            VolumeId = volumeId,
            VolumePath = volumePath,
            RootPath = relativeRootPath,
            DirId = 0,
            CreatedAt = now,
            LastScannedAt = now,
            VolumeLabel = volume?.Label,
            IsRotational = volume?.IsRotational,
            FileSystemType = volume?.FileSystemType,
            DevicePath = volume?.DevicePath,
            DeviceModel = volume?.DeviceModel,
            IsDeleted = false,
            DeletedAtUtc = null
        };

        UpsertScanRoot_NoLock(created);

        MarkMetaDirty_NoLock();
        return created;
    }

    Task IRepoInternal.CommitScanRootSnapshotV2Async(ScanRootSnapshotV2 snapshot, CancellationToken cancellationToken)
        => CommitAndPublishSnapshotAsync(snapshot, RepoSnapshotCommitReason.Maintenance, cancellationToken);

    async Task IRepoInternal.FinaliseCompletedScanAsync(long scanSequence, ScanRootSnapshotV2 completedSnapshot, CancellationToken ct)
    {
        var (generation, snapshotView, updatedRun) =
            await FinaliseCompletedScanAsync(scanSequence, completedSnapshot, ct).ConfigureAwait(false);

        PublishEvent(new ScanRunFinalisedEvent
        {
            Generation = generation,
            Run = updatedRun
        });

        PublishEvent(new ScanRootSnapshotReplacedEvent
        {
            Generation = generation,
            ScanRootId = completedSnapshot.ScanRootId,
            RepoSnapshotView = snapshotView,
            Reason = RepoSnapshotCommitReason.ScanCompleted
        });
    }

    private async Task CommitAndPublishSnapshotAsync(ScanRootSnapshotV2 snapshot, RepoSnapshotCommitReason reason, CancellationToken ct)
    {
        var (generation, snapshotView) = await CommitSnapshot_NoEventAsync(snapshot, ct).ConfigureAwait(false);

        PublishEvent(new ScanRootSnapshotReplacedEvent
        {
            Generation = generation,
            ScanRootId = snapshot.ScanRootId,
            RepoSnapshotView = snapshotView,
            Reason = reason
        });
    }

    // Caller must lock _sync
    private (long Generation, RepoSnapshotView SnapshotView) CommitSnapshotInMemory_NoLock(
        ScanRootSnapshotV2 snapshot,
        Action? additionalInMemoryChanges = null)
    {
        UpsertScanRootSnapshot_NoLock(snapshot);

        additionalInMemoryChanges?.Invoke();

        var generation = _meta.Generation + 1;
        _meta = _meta with { Generation = generation };
        MarkMetaDirty_NoLock();

        var view = GetRepoSnapshotView_NoLock();
        return (generation, view);
    }

    private async Task PersistCommittedSnapshotAsync(ScanRootSnapshotV2 snapshot, CancellationToken ct)
    {
        await PersistScanRootSnapshotV2Async(snapshot, ct).ConfigureAwait(false);
        await PersistMetaIfDirtyAsync(ct).ConfigureAwait(false);
    }

    private async Task<(long Generation, RepoSnapshotView SnapshotView)> CommitSnapshot_NoEventAsync(
        ScanRootSnapshotV2 snapshot,
        CancellationToken ct)
    {
        long gen;
        RepoSnapshotView view;

        lock (_sync)
        {
            (gen, view) = CommitSnapshotInMemory_NoLock(snapshot);
        }

        await PersistCommittedSnapshotAsync(snapshot, ct).ConfigureAwait(false);
        return (gen, view);
    }

    private async Task<(long Generation, RepoSnapshotView SnapshotView, ScanRun UpdatedRun)> FinaliseCompletedScanAsync(
        long scanSequence,
        ScanRootSnapshotV2 completedSnapshot,
        CancellationToken ct)
    {
        long gen;
        RepoSnapshotView view;
        ScanRun updatedRun;

        lock (_sync)
        {
            updatedRun = MarkScanCompleted_NoLock(scanSequence);

            (gen, view) = CommitSnapshotInMemory_NoLock(completedSnapshot);
        }

        await PersistCommittedSnapshotAsync(completedSnapshot, ct).ConfigureAwait(false);

        // Successful completion => checkpoint is no longer needed (best effort)
        try
        {
            await RepoStore.DeleteScanCheckpointAsync(_repoPath, completedSnapshot.ScanRootId, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }

        return (gen, view, updatedRun);
    }

    private ScanRun MarkScanCompleted_NoLock(long sequence)
    {
        if (!TryUpdateScanRun_NoLock(sequence, run => run with
        {
            Status = ScanRunStatus.Completed,
            FinishedAt = DateTimeOffset.UtcNow,
            ErrorMessage = null
        }, out var updated) || updated is null)
        {
            throw new InvalidOperationException($"ScanRun {sequence} was not found.");
        }

        return updated;
    }

    async Task IRepoInternal.CommitCheckpoint(ScanCheckpoint checkpoint, CancellationToken ct)
        => await RepoStore.SaveScanCheckpointAsync(_repoPath, checkpoint, ct).ConfigureAwait(false);

    public Task DeleteScanCheckpointAsync(long scanRootId, CancellationToken ct = default)
        => RepoStore.DeleteScanCheckpointAsync(_repoPath, scanRootId, ct);
}
