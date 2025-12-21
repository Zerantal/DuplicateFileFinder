using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;
using DirRecordV2 = DuplicateFileFinderLib.Repository.Storage.Models.DirRecordV2;
using FileRecordV2 = DuplicateFileFinderLib.Repository.Storage.Models.FileRecordV2;
using ScanRootSnapshotV2 = DuplicateFileFinderLib.Repository.Storage.Models.ScanRootSnapshotV2;

namespace DuplicateFileFinderLib.Repository.Core.Scan;

internal sealed class MutationBuffer(IRepoInternal repo, long scanSequence)
{
    public object Sync { get; } = new();

    private readonly PackedStringBuilder _sb = new(initialCapacityStrings: 64 * 1024, initialCapacityBytes: 8 * 1024 * 1024);

    private readonly List<DirRecordV2> _dirs = new(capacity: 64 * 1024);
    private readonly List<FileRecordV2> _files = new(capacity: 256 * 1024);

    private readonly Dictionary<long, int> _dirIdToIndex = new();
    private readonly Dictionary<long, int> _fileIdToIndex = new();

    private readonly Dictionary<(long dirId, string name), int> _fileKeyToIndex =
        new(new FileKeyComparer());

    public long UpsertDir(in DirScanInput input)
    {
        lock (Sync)
        {
            var dirId = input.DirId > 0 ? input.DirId : repo.AllocateDirId();

            var rec = new DirRecordV2
            {
                DirId = dirId,
                ParentDirId = input.ParentDirId,
                NameStrIdx = _sb.Intern(input.Name),
                CreatedTicks = input.CreatedTicks,
                ModifiedTicks = input.ModifiedTicks,
                LastSeenScanSequence = scanSequence,
                Status = input.Status,
                ErrorMessageStrIdx = _sb.InternOrMinusOne(input.ErrorMessage)
            };

            if (_dirIdToIndex.TryGetValue(dirId, out var idx))
            {
                _dirs[idx] = rec;
                return dirId;
            }

            idx = _dirs.Count;
            _dirs.Add(rec);
            _dirIdToIndex.Add(dirId, idx);
            return dirId;
        }
    }

    public void UpsertFile(in FileScanInput input)
    {
        lock (Sync)
        {
            var fileId = input.FileId > 0 ? input.FileId : repo.AllocateFileId();

            var rec = new FileRecordV2
            {
                FileId = fileId,
                DirId = input.DirId,
                NameStrIdx = _sb.Intern(input.Name),
                Size = input.Size,
                Hash = input.Hash,
                CreatedTicks = input.CreatedTicks,
                ModifiedTicks = input.ModifiedTicks,
                LastSeenScanSequence = scanSequence,
                Status = input.Status,
                ErrorMessageStrIdx = _sb.InternOrMinusOne(input.ErrorMessage)
            };

            var key = (rec.DirId, input.Name);

            if (_fileIdToIndex.TryGetValue(fileId, out var idx))
            {
                _files[idx] = rec;
                _fileKeyToIndex[key] = idx;
                return;
            }

            idx = _files.Count;
            _files.Add(rec);
            _fileIdToIndex.Add(fileId, idx);
            _fileKeyToIndex[key] = idx;
        }
    }

    public void ApplyFileHash(long dirId, string name, HashKey hash)
    {
        lock (Sync)
        {
            if (_fileKeyToIndex.TryGetValue((dirId, name), out var idx))
            {
                _files[idx] = _files[idx] with { Hash = hash };
                return;
            }

            // Fallback: if file wasn't recorded (unexpected), create an error entry
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
            if (_fileKeyToIndex.TryGetValue((dirId, name), out var idx))
            {
                _files[idx] = _files[idx] with
                {
                    Status = ScanEntryStatus.Error,
                    ErrorMessageStrIdx = _sb.Intern(errorMessage)
                };
                return;
            }

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
            return new ScanRootSnapshotV2
            {
                ScanRootId = scanRootId,
                StringPool = _sb.Build(),
                Dirs = _dirs.Where(d => d.Status != ScanEntryStatus.None).ToArray(),
                Files = _files.Where(f => f.Status != ScanEntryStatus.None).ToArray()
            };
        }
    }

    private sealed class FileKeyComparer : IEqualityComparer<(long dirId, string name)>
    {
        public bool Equals((long dirId, string name) x, (long dirId, string name) y)
            => x.dirId == y.dirId && PathUtils.PathComparer.Equals(x.name, y.name);

        public int GetHashCode((long dirId, string name) obj)
            => HashCode.Combine(obj.dirId, PathUtils.PathComparer.GetHashCode(obj.name));
    }
}