using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Repository;

public sealed partial class Repo
{
    internal long AllocateScanSequence()
    {
        lock (_sync)
        {
            return AllocateScanSequence_NoLock();
        }
    }

    private long AllocateScanSequence_NoLock()
    {
        var seq = Meta.NextScanSequence;
        Meta = Meta with { NextScanSequence = seq + 1 };

        SyncMetaFile_NoLock();
        SaveMeta_NoLock();

        return seq;
    }


    internal long AllocateLogId()
    {
        lock (_sync)
        {
            var id = Meta.NextLogSequence;
            Meta = Meta with { NextLogSequence = id + 1 };

            SyncMetaFile_NoLock();
            SaveMeta_NoLock();

            return id;
        }
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

    private void AddToHashIndex_NoLock(FileRecord f)
    {
        if (!f.Hash.IsComputed)
            return;

        if (!_hashIndex.TryGetValue(f.Hash, out var list))
        {
            list = new List<Guid>();
            _hashIndex[f.Hash] = list;
        }

        if (!list.Contains(f.FileId))
            list.Add(f.FileId);
    }

    private void RemoveFromHashIndex_NoLock(FileRecord f)
    {
        if (!f.Hash.IsComputed)
            return;

        if (_hashIndex.TryGetValue(f.Hash, out var list))
        {
            list.Remove(f.FileId);
            if (list.Count == 0)
                _hashIndex.Remove(f.Hash);
        }
    }

    
    internal void MarkScanCompleted(long sequence)
    {
        lock (_sync)
        {
            if (!_scanRunIndex.TryGetValue(sequence, out var run))
                return;

            var updated = run with
            {
                Status = ScanRunStatus.Completed,
                FinishedAt = DateTimeOffset.UtcNow,
                ErrorMessage = null
            };

            _scanRunIndex[sequence] = updated;
            var idx = _scanRuns.FindIndex(r => r.ScanSequence == sequence);
            if (idx >= 0) _scanRuns[idx] = updated;
            else _scanRuns.Add(updated);
            
            SyncMetaFile_NoLock();
            _ = PersistMetaAsync();
        }
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
    
    internal void CompleteScanForRoot(long scanSequence, string rootPath)
    {
        List<FileTombstone> deletedFiles;
        List<DirTombstone>  deletedDirs;

        lock (_sync)
        {
            deletedFiles = new List<FileTombstone>();
            deletedDirs  = new List<DirTombstone>();

            foreach (var file in _files.Values)
            {
                if (!IsUnderRoot(file.DirId, rootPath))
                    continue;

                if (file.LastSeenScanSequence < scanSequence)
                    deletedFiles.Add(new FileTombstone(file.FileId, scanSequence));
            }
            
            foreach (var dir in _dirs.Values)
            {
                if (!IsUnderRoot(dir.DirId, rootPath))
                    continue;

                if (dir.LastSeenSequence < scanSequence)
                    deletedDirs.Add(new DirTombstone(dir.DirId, scanSequence));
            }
        }

        if (deletedFiles.Count == 0 && deletedDirs.Count == 0)
        {
            MarkScanCompleted(scanSequence);
            return;
        }

        var tombstoneDelta = new RepoDelta
        {
            ScanSequence = scanSequence,
            Files = new List<FileRecord>(),
            Dirs = new List<DirRecord>(),
            DeletedFiles = deletedFiles,
            DeletedDirs = deletedDirs
        };

        CommitDelta(tombstoneDelta);
        MarkScanCompleted(scanSequence);
    }

    
    private bool IsUnderRoot(Guid dirId, string rootPath)
    {
        var path = GetFullDirPath(dirId);
        return path.StartsWith(rootPath, PathUtils.PathComparison);
    }
    
    // Find existing ScanRoot by canonical path or create a new one.
    private ScanRoot FindOrCreateScanRoot_NoLock(string normalizedRootPath)
    {
        foreach (var root in _scanRoots.Values)
        {
            if (string.Equals(root.RootPath, normalizedRootPath, StringComparison.Ordinal))
                return root;
        }

        var now = DateTimeOffset.UtcNow;

        var newRoot = new ScanRoot
        {
            Id            = Guid.NewGuid(),
            RootPath      = normalizedRootPath,
            DirId         = Guid.Empty,
            CreatedAt     = now,
            LastScannedAt = now,
            VolumeId      = null,
            VolumeLabel   = null,
            IsRotational  = null,
            FileSystemType = null,
            DevicePath    = null,
            DeviceModel   = null
        };

        _scanRoots[newRoot.Id] = newRoot;
        
        SyncMetaFile_NoLock();
        _ = PersistMetaAsync();

        return newRoot;
    }

    internal void BindScanRootDirId(Guid scanRootId, Guid dirId)
    {
        lock (_sync)
        {
            if (!_scanRoots.TryGetValue(scanRootId, out var root))
                return;

            // Already bound to this dir: nothing to do
            if (root.DirId == dirId)
                return;

            // First-time bind, or rebind if it was Guid.Empty
            if (root.DirId == Guid.Empty || root.DirId == dirId)
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