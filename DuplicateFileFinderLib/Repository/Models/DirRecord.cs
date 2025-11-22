using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record DirRecord
{
    [MemoryPackOrder(0)] public required Guid Id { get; init; }
    [MemoryPackOrder(1)] public required Guid? ParentId { get; init; }
    [MemoryPackOrder(2)] public required string Name { get; init; }
    [MemoryPackOrder(3)] public required long LastSeenSequence { get; init; }
    [MemoryPackOrder(4)] public required ScanEntryStatus Status { get; init; }
    [MemoryPackOrder(5)] public string? ErrorMessage { get; init; }

    // Possible Extensions:
    // [MemoryPackOrder(7)] ulong? INode {get; init;}   // or FileId on Windows (might need to be byte[])
    // [MemoryPackOrder(8)] ulong? DeviceId ErrorMessage { get; init; }
}