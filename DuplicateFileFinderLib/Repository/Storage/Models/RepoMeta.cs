using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage.Models;

[MemoryPackable]
public partial record RepoMeta
{
    [MemoryPackOrder(0)] public required int SchemaVersion { get; init; } = 6;
    // Generation is incremented for each externally visible state mutation
    [MemoryPackOrder(1)] public required long Generation { get; init; } = 1;
    [MemoryPackOrder(5)] public required Guid RepoId { get; init; } = Guid.NewGuid();
    [MemoryPackOrder(6)] public required string RepoPath { get; init; }
    [MemoryPackOrder(7)] public required string RepoHostName { get; init; }
    [MemoryPackOrder(8)] public required long NextScanSequence { get; init; } = 1;
    [MemoryPackOrder(8)] public ScanRootId NextScanRootId { get; init; } = 1;
    [MemoryPackOrder(10)] public DirId NextDirId { get; init; } = 1;
    [MemoryPackOrder(11)] public FileId NextFileId { get; init; } = 1;

}
