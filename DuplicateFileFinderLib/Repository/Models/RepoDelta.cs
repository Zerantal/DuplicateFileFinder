using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public sealed partial record RepoDelta
{
    [MemoryPackOrder(0)] public required long ScanSequence { get; init; }
    [MemoryPackOrder(1)] public IReadOnlyList<FileRecord> Files { get; init; } = [];
    [MemoryPackOrder(2)] public IReadOnlyList<DirRecord> Dirs { get; init; } = [];
}