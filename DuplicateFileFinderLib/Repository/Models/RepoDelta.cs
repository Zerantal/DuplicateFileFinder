using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public sealed partial record FileTombstone(Guid FileId, long ScanSequence);

[MemoryPackable]
public sealed partial record DirTombstone(Guid DirId, long ScanSequence);

[MemoryPackable]
public sealed partial record RepoDelta
{
    [MemoryPackOrder(0)] public required long ScanSequence { get; init; }
    [MemoryPackOrder(1)] public IReadOnlyList<FileRecord> Files { get; init; } = [];

    [MemoryPackOrder(2)] public IReadOnlyList<DirRecord> Dirs { get; init; } = [];

    [MemoryPackOrder(3)] public IReadOnlyList<FileTombstone> DeletedFiles { get; init; } = [];
    [MemoryPackOrder(4)] public IReadOnlyList<DirTombstone> DeletedDirs { get; init; } = [];
}