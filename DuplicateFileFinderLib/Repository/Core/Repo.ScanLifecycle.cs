using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed partial class Repo
{
    public long AllocateRunId()
    {
        lock (_sync)
        {
            var id = AllocateRunId_NoLock();
            SaveMeta_NoLock();
            return id;
        }
    }

    private long AllocateRunId_NoLock()
    {
        var seq = Meta.NextScanSequence;
        Meta = Meta with { NextScanSequence = seq + 1 };

        SyncMetaFile_NoLock();
        SaveMeta_NoLock();

        return seq;
    }

    private long AllocateLogId_NoLock()
    {
        var id = Meta.NextLogSequence;
        Meta = Meta with { NextLogSequence = id + 1 };
        return id;
    }
    
    internal long AllocateLogId()
    {
        lock (_sync)
        {
            var id = AllocateLogId_NoLock();
            SaveMeta_NoLock();
            return id;
        }
    }

    internal long AllocateDirId()
    {
        lock (_sync)
        {
            return AllocateDirId_NoLock();
        }
    }
    
    internal long AllocateDirId_NoLock()
    {
        var id = Meta.NextDirId;
        Meta = Meta with { NextDirId = id + 1 };
        return id;
    }

    internal long AllocateFileId()
    {
        lock (_sync)
        {
            return AllocateFileId_NoLock();
        }
    }

    internal long AllocateFileId_NoLock()
    {
        var id = Meta.NextFileId;
        Meta = Meta with { NextFileId = id + 1 };
        return id;
    }

    internal long AllocateRootId_NoLock()
    {
        var id = Meta.NextScanRootId;
        Meta = Meta with { NextScanRootId = id + 1 };
        return id;
    }
    
    // Caller holds _sync
    private void SaveMeta_NoLock()
    {
        _metaFile = new RepoMetaFile
        {
            Meta      = Meta,
            ScanRoots = _scanRoots.Values.ToList(),
            ScanRuns  = _scanRuns.ToList()
        };

        RepoStore.SaveMetaAsync(_repoPath, _metaFile, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }
    
    internal void MarkScanCompleted(long sequence)
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

            generation = Meta.Generation;
            SaveMeta_NoLock();
        }

        OnScanRunCompleted(generation, sequence, updated);
    }
    
    private void OnScanRunCompleted(long generation, long nextLogSequence, ScanRun run)
    {
        var evt = new ScanRunCompletedEvent
        {
            Generation      = generation,
            NextLogSequence = nextLogSequence,
            Run             = run
        };

        PublishEvent(evt);
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
            var idx = _scanRuns.FindIndex(r => r.ScanSequence == sequence);
            if (idx >= 0) _scanRuns[idx] = updated;
            else _scanRuns.Add(updated);
            
            SyncMetaFile_NoLock();
            _ = PersistMetaAsync();
        }
    }
    
    private bool IsUnderRoot(long dirId, string rootPath)
    {
        var path = GetFullDirPath(dirId);
        return path.StartsWith(rootPath, PathUtils.PathComparison);
    }
    
    // Find existing ScanRoot by canonical path or create a new one.
    private ScanRoot FindOrCreateScanRoot_NoLock(string? volumePath, string relativeRootPath)
    {
        foreach (var root in _scanRoots.Values.Where(r => !r.IsDeleted))
        {
            if (string.Equals(root.VolumePath, volumePath, StringComparison.Ordinal) &&
                string.Equals(root.RootPath,  relativeRootPath, StringComparison.Ordinal))
            {
                return root;
            }
        }

        var now = DateTimeOffset.UtcNow;

        var newRoot = new ScanRoot
        {
            RootId        = AllocateRootId_NoLock(),
            VolumePath     = volumePath,
            RootPath       = relativeRootPath,   // now relative to VolumePath
            DirId         = 0,
            CreatedAt     = now,
            LastScannedAt = now,
            VolumeId      = null,
            VolumeLabel   = null,
            IsRotational  = null,
            FileSystemType = null,
            DevicePath    = null,
            DeviceModel   = null,
            IsDeleted     = false,
            DeletedAtUtc  = null
        };

        _scanRoots[newRoot.RootId] = newRoot;
        SyncMetaFile_NoLock();
        _ = PersistMetaAsync();

        return newRoot;
    }

    internal void BindScanRootDirId(long scanRootId, long dirId)
    {
        lock (_sync)
        {
            if (!_scanRoots.TryGetValue(scanRootId, out var root))
                return;
    
            // Already bound to this dir: nothing to do
            if (root.DirId == dirId)
                return;
    
            // First-time bind, or rebind if it was Guid.Empty
            if (root.DirId == 0 || root.DirId == dirId)
            {
                _scanRoots[scanRootId] = root with { DirId = dirId };
                SyncMetaFile_NoLock();
                SaveMeta_NoLock();
            }
            else
            {
                // TODO: Log warning about conflicting DirId?
            }
        }
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
}