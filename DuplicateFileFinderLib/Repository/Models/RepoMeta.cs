using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record RepoMeta
{
    [MemoryPackOrder(0)] public required int SchemaVersion { get; init; } = 5;
    [MemoryPackOrder(1)] public required long Generation { get; init; } = 1;
    [MemoryPackOrder(2)] public required long NextLogSequence { get; init; }
    [MemoryPackOrder(3)] public required long LastSnapshottedLogSequence { get; init; } = -1;
    [MemoryPackOrder(4)] public DateTimeOffset LastCompaction { get; init; } = DateTimeOffset.UtcNow;
    [MemoryPackOrder(5)] public required Guid RepoId { get; init; }
    [MemoryPackOrder(6)] public required string RepoPath { get; init; }
    [MemoryPackOrder(7)] public required string RepoHostName { get; init; }
    [MemoryPackOrder(8)] public required long NextScanSequence { get; init; }
}