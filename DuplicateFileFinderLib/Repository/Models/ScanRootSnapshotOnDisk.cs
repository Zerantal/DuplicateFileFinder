// Repository/Models/ScanRootSnapshotOnDisk.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record ScanRootSnapshotOnDisk
{
    [MemoryPackOrder(0)] public required long ScanRootId { get; init; }
    [MemoryPackOrder(1)] public required DirRecord[] Dirs { get; init; } = [];
    [MemoryPackOrder(2)] public required FileRecord[] Files { get; init; } = [];
}