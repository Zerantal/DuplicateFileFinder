using DirRecordV2 = DuplicateFileFinderLib.Repository.Storage.Models.DirRecordV2;
using FileRecordV2 = DuplicateFileFinderLib.Repository.Storage.Models.FileRecordV2;
using PackedStringPool = DuplicateFileFinderLib.Repository.Storage.Models.PackedStringPool;

namespace DuplicateFileFinderLib.Repository.Core.Models;

public sealed class ScanRootSnapshotView
{
    public required long ScanRootId { get; init; }
    public required PackedStringPool StringPool { get; init; }
    public required IReadOnlyList<DirRecordV2> Dirs { get; init; }
    public required IReadOnlyList<FileRecordV2> Files { get; init; }
}
