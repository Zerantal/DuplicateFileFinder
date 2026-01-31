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

    public DirId UpsertDir(in DirScanInput input)
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

    public void ApplyFileHash(DirId dirId, string name, HashKey hash)
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

    public void ApplyFileError(DirId dirId, string name, string errorMessage)
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

    internal ScanRootSnapshotV2 BuildSnapshotV2(ScanRootId scanRootId)
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
    internal ScanRootSnapshotV2 DrainCheckpointSnapshot(ScanRootId scanRootId)
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

    private static ScanRootSnapshotV2 BuildSnapshotFrom(Buffer b, ScanRootId scanRootId)
        => new()
        {
            ScanRootId = scanRootId,
            StringPool = b.Sb.Build(),
            Dirs = b.Dirs.Where(d => d.Status != ScanEntryStatus.None).ToArray(),
            Files = b.Files.Where(f => f.Status != ScanEntryStatus.None).ToArray()
        };

    private void UpsertDirInto(Buffer b, in DirScanInput input, DirId dirId)
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

    private void UpsertFileInto(Buffer b, in FileScanInput input, FileId fileId)
    {
        // Build a candidate record from the new observation.
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
            var existing = b.Files[idx];

            b.Files[idx] = MergeFile(existing, rec);
            b.FileKeyToIndex[key] = idx;
            return;
        }

        // New record: ensure status is consistent with hash.
        rec = NormalizeNewFile(rec);

        idx = b.Files.Count;
        b.Files.Add(rec);
        b.FileIdToIndex.Add(fileId, idx);
        b.FileKeyToIndex[key] = idx;
    }

    private static FileRecordV2 NormalizeNewFile(FileRecordV2 rec)
    {
        // Ensure basic invariants:
        // - if hash is computed => Hashed unless explicitly Deleted/Error
        if (rec.Status is ScanEntryStatus.Deleted or ScanEntryStatus.Error)
            return rec;

        if (rec.Hash != HashKey.NotComputed)
        {
            rec = rec with
            {
                Status = ScanEntryStatus.Hashed,
                ErrorMessageStrIdx = -1
            };
        }
        else if (rec.Status == ScanEntryStatus.Hashed)
        {
            // Guard: Hashed implies computed hash.
            rec = rec with { Status = ScanEntryStatus.Enumerated };
        }

        return rec;
    }

    private static FileRecordV2 MergeFile(FileRecordV2 existing, FileRecordV2 incoming)
    {
        // Deleted/Error are explicit states: honor incoming if it declares them.
        if (incoming.Status == ScanEntryStatus.Deleted)
        {
            // For deleted entries, we keep the record but mark deleted. Hash is not meaningful.
            return incoming with
            {
                Hash = HashKey.NotComputed,
                ErrorMessageStrIdx = -1
            };
        }

        if (incoming.Status == ScanEntryStatus.Error)
        {
            // Error entry: keep as error; hash not meaningful unless you want to preserve old hash.
            // (Keep NotComputed to avoid "hashed but error".)
            return incoming with
            {
                Hash = HashKey.NotComputed
            };
        }

        // If incoming supplies a computed hash (baseline reuse or newly hashed),
        // that is authoritative for this observation.
        if (incoming.Hash != HashKey.NotComputed)
        {
            return incoming with
            {
                Status = ScanEntryStatus.Hashed,
                ErrorMessageStrIdx = -1
            };
        }

        // Incoming does not supply a hash. Decide whether we can preserve the existing hash.
        var existingHasHash = existing.Hash != HashKey.NotComputed;

        if (existingHasHash)
        {
            var changed =
                existing.Size != incoming.Size ||
                existing.ModifiedTicks != incoming.ModifiedTicks;

            if (!changed)
            {
                // Unchanged: preserve hash and keep Hashed.
                return incoming with
                {
                    Hash = existing.Hash,
                    Status = ScanEntryStatus.Hashed,
                    ErrorMessageStrIdx = -1
                };
            }

            // Changed: invalidate hash; enumeration indicates it exists but needs rehashing.
            return incoming with
            {
                Hash = HashKey.NotComputed,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessageStrIdx = -1
            };
        }

        // No hash to preserve; keep enumerated.
        // Also normalize "Hashed with no hash" if that ever occurs.
        if (incoming.Status == ScanEntryStatus.Hashed)
            return incoming with { Status = ScanEntryStatus.Enumerated };

        return incoming;
    }

    private void EnsureDeltaFileExists(DirId dirId, string name)
    {
        if (_delta.FileKeyToIndex.ContainsKey((dirId, name)))
            return;

        // If full has the record, copy numeric fields; otherwise use defaults.
        FileId fileId = -1;
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

        // Normalize in case we copied a computed hash but stale status.
        rec = NormalizeNewFile(rec);

        var idx = _delta.Files.Count;
        _delta.Files.Add(rec);
        _delta.FileIdToIndex[rec.FileId] = idx;
        _delta.FileKeyToIndex[(dirId, name)] = idx;
    }

    private static bool TryUpdateFileHashInBuffer(Buffer b, DirId dirId, string name, HashKey hash)
    {
        if (!b.FileKeyToIndex.TryGetValue((dirId, name), out var idx))
            return false;

        b.Files[idx] = b.Files[idx] with
        {
            Hash = hash,
            Status = ScanEntryStatus.Hashed,
            ErrorMessageStrIdx = -1,
        };
        return true;
    }

    private static bool TryUpdateFileErrorInBuffer(Buffer b, DirId dirId, string name, string errorMessage)
    {
        if (!b.FileKeyToIndex.TryGetValue((dirId, name), out var idx))
            return false;

        b.Files[idx] = b.Files[idx] with
        {
            Status = ScanEntryStatus.Error,
            Hash = HashKey.NotComputed,
            ErrorMessageStrIdx = b.Sb.Intern(errorMessage)
        };
        return true;
    }

    private sealed class FileKeyComparer : IEqualityComparer<(DirId dirId, string name)>
    {
        public bool Equals((DirId dirId, string name) x, (DirId dirId, string name) y)
            => x.dirId == y.dirId && PathUtils.PathComparer.Equals(x.name, y.name);

        public int GetHashCode((DirId dirId, string name) obj)
            => HashCode.Combine(obj.dirId, PathUtils.PathComparer.GetHashCode(obj.name));
    }

    private sealed class Buffer
    {
        public readonly PackedStringBuilder Sb =
            new(initialCapacityStrings: 8 * 1024, initialCapacityBytes: 1 * 1024 * 1024);

        public readonly List<DirRecordV2> Dirs = new();
        public readonly List<FileRecordV2> Files = new();

        public readonly Dictionary<DirId, int> DirIdToIndex = new();
        public readonly Dictionary<FileId, int> FileIdToIndex = new();
        public readonly Dictionary<(DirId dirId, string name), int> FileKeyToIndex =
            new(new FileKeyComparer());
    }
}
