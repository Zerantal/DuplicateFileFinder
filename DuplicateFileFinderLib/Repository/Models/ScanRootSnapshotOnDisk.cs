// Repository/Models/ScanRootSnapshotOnDisk.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record ScanRootSnapshotOnDisk
{
    [MemoryPackOrder(0)] public required Guid ScanRootId { get; init; }

    // All dirs that belong to this scan root
    [MemoryPackOrder(1)] public required DirRecord[] Dirs { get; init; } = [];

    // All files that belong to this scan root
    [MemoryPackOrder(2)] public required FileRecord[] Files { get; init; } = [];
}