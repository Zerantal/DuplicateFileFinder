using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public sealed partial record FileTombstone(long FileId, long RunId);

[MemoryPackable]
public sealed partial record DirTombstone(long DirId, long RunId);

[MemoryPackable]
public sealed partial record RepoDelta
{
    [MemoryPackOrder(0)] public required long RunId { get; init; }
    [MemoryPackOrder(1)] public IReadOnlyList<FileRecord> Files { get; init; } = [];

    [MemoryPackOrder(2)] public IReadOnlyList<DirRecord> Dirs { get; init; } = [];

    [MemoryPackOrder(3)] public IReadOnlyList<FileTombstone> DeletedFiles { get; init; } = [];
    [MemoryPackOrder(4)] public IReadOnlyList<DirTombstone> DeletedDirs { get; init; } = [];
}