// Repository/Models/ScanRootSnapshot.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
internal partial record ScanRootSnapshot
{
    [MemoryPackOrder(0)] public required long ScanRootId { get; init; }
    
    [MemoryPackOrder(1)] public required DirRecord[] Dirs { get; init; } = [];
    [MemoryPackOrder(2)] public required FileRecord[] Files { get; init; } = [];
}