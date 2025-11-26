using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository;

public sealed partial class Repo
{
    internal long AllocateScanSequence()
    {
        lock (_sync)
        {
            var seq = _meta.NextScanSequence;
            _meta = _meta with { NextScanSequence = seq + 1 };
            SaveMeta_NoLock();
            return seq;
        }
    }

    internal long AllocateLogId()
    {
        lock (_sync)
        {
            var id = _meta.NextLogSequence;
            _meta = _meta with { NextLogSequence = id + 1 };
            SaveMeta_NoLock();
            return id;
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
            
            SaveScanRuns_NoLock(); // persist "completed" status
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
            
            SaveScanRuns_NoLock(); // persist "failed/cancelled" status
        }
    }
    
    internal void CompleteScanForRoot(long scanSequence, string rootPath)
    {
        // Compute which files/dirs under root were *not* seen at scanSequence
        var deletedFiles = new List<FileTombstone>();
        var deletedDirs = new List<DirTombstone>();

        foreach (var kvp in _files)
        {
            var file = kvp.Value;
            if (!IsUnderRoot(file.DirId, rootPath))
                continue;

            if (file.LastSeenScanSequence < scanSequence)
                deletedFiles.Add(new FileTombstone(file.Id, scanSequence));
        }

        foreach (var kvp in _dirs)
        {
            var dir = kvp.Value;
            if (!IsUnderRoot(dir.Id, rootPath))
                continue;

            if (dir.LastSeenSequence < scanSequence)
                deletedDirs.Add(new DirTombstone(dir.Id, scanSequence));
        }

        if (deletedFiles.Count == 0 && deletedDirs.Count == 0)
        {
            // Nothing to tombstone, just mark completed
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
        return path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
    }
}