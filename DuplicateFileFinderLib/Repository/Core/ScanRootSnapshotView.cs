using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Repository.Core;

public sealed class ScanRootSnapshotView
{
    public required long ScanRootId { get; init; }
    public required PackedStringPool StringPool { get; init; }
    public required IReadOnlyList<DirRecordV2> Dirs { get; init; }
    public required IReadOnlyList<FileRecordV2> Files { get; init; }
}