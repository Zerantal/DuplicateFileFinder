using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record FileRecord
{
    [MemoryPackOrder(10)] public required long FileId { get; init; }
    [MemoryPackOrder(11)] public required long DirId { get; init; }
    [MemoryPackOrder(2)] public required string Name { get; init; }
    [MemoryPackOrder(3)] public long Size { get; init; }
    [MemoryPackOrder(4)] public HashKey Hash { get; init; }
    [MemoryPackOrder(5)] public DateTimeOffset Modified { get; init; }
    [MemoryPackOrder(6)] public DateTimeOffset Created { get; init; }
    [MemoryPackOrder(7)] public required long LastSeenScanSequence { get; init; }
    [MemoryPackOrder(8)] public required ScanEntryStatus Status { get; init; }
    [MemoryPackOrder(9)] public string? ErrorMessage { get; init; }

    // Future enhancement? enable detecting moves without rescanning
    // public ulong? Inode { get; init; } // or FileId on Windows
    // public ulong? DeviceId { get; init; }
}