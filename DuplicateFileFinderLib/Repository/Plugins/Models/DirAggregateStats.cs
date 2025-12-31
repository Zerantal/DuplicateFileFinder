using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Plugins.Models;

[MemoryPackable]
public sealed partial record DirAggregateStats
{
    [MemoryPackOrder(0)] public required long TotalBytes { get; init; }
    [MemoryPackOrder(1)] public required int FileCount { get; init; }
    [MemoryPackOrder(2)] public required int DirCount { get; init; } // descendant dirs, excluding self
    [MemoryPackOrder(3)] public required long DuplicateFiles { get; init; }
    [MemoryPackOrder(3)] public required long DuplicateBytes { get; init; }
}
