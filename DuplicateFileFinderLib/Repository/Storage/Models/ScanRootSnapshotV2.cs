// Repository/Models/ScanRootSnapshot.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Storage.Models;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial record struct ScanRootSnapshotV2
{
    [MemoryPackOrder(0)] public required long ScanRootId { get; init; }
    [MemoryPackOrder(1)] public required PackedStringPool StringPool { get; init; }
    [MemoryPackOrder(2)] public required DirRecordV2[] Dirs { get; init; }
    [MemoryPackOrder(3)] public required FileRecordV2[] Files { get; init; }
}
