using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Repository.Core.Scan;

internal sealed class MutationBuffer(IRepoInternal repo, long scanSequence)
{
    public object Sync { get; } = new();

    // Authoritative in-flight state used for final BuildSnapshotV2()
    private readonly Buffer _full = new();

    // Delta since last checkpoint; drained by DrainCheckpointSnapshot()
    private Buffer _delta = new();

    // Reused buffer to avoid allocations when draining
    private Buffer _drain = new();

    public long UpsertDir(in DirScanInput input)
    {
        lock (Sync)
        {
            var dirId = input.DirId > 0 ? input.DirId : repo.AllocateDirId();
            
            UpsertDirInto(_full, input, dirId);
            UpsertDirInto(_delta, input, dirId);

            return dirId;
        }
    }

    public void UpsertFile(in FileScanInput input)
    {
        lock (Sync)
        {
            var fileId = input.FileId > 0 ? input.FileId : repo.AllocateFileId();

            UpsertFileInto(_full, input, fileId);
            UpsertFileInto(_delta, input, fileId);
        }
    }

    public void ApplyFileHash(long dirId, string name, HashKey hash)
    {
        lock (Sync)
        {
            // Update full state (authoritative)
            if (TryUpdateFileHashInBuffer(_full, dirId, name, hash))
            {
                EnsureDeltaFileExists(dirId, name);
                TryUpdateFileHashInBuffer(_delta, dirId, name, hash);
                return;
            }

            // Fallback: if file wasn't recorded (unexpected), create an error entry in both
            var f = new FileScanInput
            {
                FileId = -1,
                DirId = dirId,
                Name = name,
                Size = 0,
                Hash = hash,
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.Error,
                ErrorMessage = "Hash produced for unknown file (no prior OnFileFound)."
            };
            UpsertFile(f);
        }
    }

    public void ApplyFileError(long dirId, string name, string errorMessage)
    {
        lock (Sync)
        {
            // Update full state (authoritative)
            if (TryUpdateFileErrorInBuffer(_full, dirId, name, errorMessage))
            {
                EnsureDeltaFileExists(dirId, name);
                TryUpdateFileErrorInBuffer(_delta, dirId, name, errorMessage);
                return;
            }

            // If file wasn't recorded, create an error entry in both
            var f = new FileScanInput
            {
                FileId = -1,
                DirId = dirId,
                Name = name,
                Size = 0,
                Hash = HashKey.NotComputed,
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.Error,
                ErrorMessage = errorMessage
            };
            UpsertFile(f);
        }
    }

    internal ScanRootSnapshotV2 BuildSnapshotV2(long scanRootId)
    {
        lock (Sync)
        {
            return BuildSnapshotFrom(_full, scanRootId);
        }
    }

    /// <summary>
    /// Drains only the delta buffer (changes since last checkpoint).
    /// This MUST NOT affect the full buffer used to build the final snapshot.
    /// </summary>
    internal ScanRootSnapshotV2 DrainCheckpointSnapshot(long scanRootId)
    {
        lock (Sync)
        {
            // Swap delta into drain buffer
            (_delta, _drain) = (_drain, _delta);

            // Reset new delta buffer for subsequent mutations
            ClearBuffer(_delta);

            // Build snapshot from drained delta
            var snap = BuildSnapshotFrom(_drain, scanRootId);
            
            // Clear drained buffer so it can be reused next time
            ClearBuffer(_drain);

            return snap;
        }
    }

    // -------------------- Helpers --------------------

    private static void ClearBuffer(Buffer b)
    {
        b.Dirs.Clear();
        b.Files.Clear();
        b.DirIdToIndex.Clear();
        b.FileIdToIndex.Clear();
        b.FileKeyToIndex.Clear();
        b.Sb.Reset();
    }

    private static ScanRootSnapshotV2 BuildSnapshotFrom(Buffer b, long scanRootId)
        => new()
            {
                ScanRootId = scanRootId,
                StringPool = b.Sb.Build(),
                Dirs = b.Dirs.Where(d => d.Status != ScanEntryStatus.None).ToArray(),
                Files = b.Files.Where(f => f.Status != ScanEntryStatus.None).ToArray()
            };

    private void UpsertDirInto(Buffer b, in DirScanInput input, long dirId)
    {
        var rec = new DirRecordV2
        {
            DirId = dirId,
            ParentDirId = input.ParentDirId,
            NameStrIdx = b.Sb.Intern(input.Name),
            CreatedTicks = input.CreatedTicks,
            ModifiedTicks = input.ModifiedTicks,
            LastSeenScanSequence = scanSequence,
            Status = input.Status,
            ErrorMessageStrIdx = b.Sb.InternOrMinusOne(input.ErrorMessage)
        };

        if (b.DirIdToIndex.TryGetValue(dirId, out var idx))
        {
            b.Dirs[idx] = rec;
            return;
        }

        idx = b.Dirs.Count;
        b.Dirs.Add(rec);
        b.DirIdToIndex.Add(dirId, idx);
        }

    private void UpsertFileInto(Buffer b, in FileScanInput input, long fileId)
    {
        var rec = new FileRecordV2
        {
            FileId = fileId,
            DirId = input.DirId,
            NameStrIdx = b.Sb.Intern(input.Name),
            Size = input.Size,
            Hash = input.Hash,
            CreatedTicks = input.CreatedTicks,
            ModifiedTicks = input.ModifiedTicks,
            LastSeenScanSequence = scanSequence,
            Status = input.Status,
            ErrorMessageStrIdx = b.Sb.InternOrMinusOne(input.ErrorMessage)
        };

        var key = (rec.DirId, input.Name);

        if (b.FileIdToIndex.TryGetValue(fileId, out var idx))
        {
            b.Files[idx] = rec;
            b.FileKeyToIndex[key] = idx;
            return;
        }

        idx = b.Files.Count;
        b.Files.Add(rec);
        b.FileIdToIndex.Add(fileId, idx);
        b.FileKeyToIndex[key] = idx;
    }

    private void EnsureDeltaFileExists(long dirId, string name)
    {
        if (_delta.FileKeyToIndex.ContainsKey((dirId, name)))
            return;

        // If full has the record, copy numeric fields; otherwise use defaults.
        long fileId = -1;
        long size = 0;
        long created = 0;
        long modified = 0;
        var status = ScanEntryStatus.Enumerated;
        var hash = HashKey.NotComputed;
        string? err = null;

        if (_full.FileKeyToIndex.TryGetValue((dirId, name), out var fullIdx))
        {
            var fr = _full.Files[fullIdx];
            fileId = fr.FileId;
            size = fr.Size;
            created = fr.CreatedTicks;
            modified = fr.ModifiedTicks;
            status = fr.Status;
            hash = fr.Hash;
            // err: we cannot decode the existing message; that's fine for ensuring presence.
        }

        // Create a delta record keyed by (dirId,name). Intern the provided 'name' directly.
        var rec = new FileRecordV2
        {
            FileId = fileId > 0 ? fileId : repo.AllocateFileId(),
            DirId = dirId,
            NameStrIdx = _delta.Sb.Intern(name),
            Size = size,
            Hash = hash,
            CreatedTicks = created,
            ModifiedTicks = modified,
            LastSeenScanSequence = scanSequence,
            Status = status,
            ErrorMessageStrIdx = err is null ? -1 : _delta.Sb.Intern(err)
        };

        var idx = _delta.Files.Count;
        _delta.Files.Add(rec);
        _delta.FileIdToIndex[rec.FileId] = idx;
        _delta.FileKeyToIndex[(dirId, name)] = idx;
    }


    private static bool TryUpdateFileHashInBuffer(Buffer b, long dirId, string name, HashKey hash)
    {
        if (!b.FileKeyToIndex.TryGetValue((dirId, name), out var idx))
            return false;

        b.Files[idx] = b.Files[idx] with { Hash = hash };
        return true;
    }

    private static bool TryUpdateFileErrorInBuffer(Buffer b, long dirId, string name, string errorMessage)
    {
        if (!b.FileKeyToIndex.TryGetValue((dirId, name), out var idx))
            return false;

        b.Files[idx] = b.Files[idx] with
        {
            Status = ScanEntryStatus.Error,
            ErrorMessageStrIdx = b.Sb.Intern(errorMessage)
        };
        return true;
    }

    private sealed class FileKeyComparer : IEqualityComparer<(long dirId, string name)>
    {
        public bool Equals((long dirId, string name) x, (long dirId, string name) y)
            => x.dirId == y.dirId && PathUtils.PathComparer.Equals(x.name, y.name);

        public int GetHashCode((long dirId, string name) obj)
            => HashCode.Combine(obj.dirId, PathUtils.PathComparer.GetHashCode(obj.name));
    }
    
    private sealed class Buffer
    {
        public readonly PackedStringBuilder Sb =
            new(initialCapacityStrings: 8 * 1024, initialCapacityBytes: 1 * 1024 * 1024);

        public readonly List<DirRecordV2> Dirs = new();
        public readonly List<FileRecordV2> Files = new();

        public readonly Dictionary<long, int> DirIdToIndex = new();
        public readonly Dictionary<long, int> FileIdToIndex = new();
        public readonly Dictionary<(long dirId, string name), int> FileKeyToIndex =
            new(new FileKeyComparer());
    }
}
