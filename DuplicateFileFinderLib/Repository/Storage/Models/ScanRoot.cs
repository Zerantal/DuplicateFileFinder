using MemoryPack;
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace DuplicateFileFinderLib.Repository.Storage.Models;

[MemoryPackable]
public partial record ScanRoot
{
    [MemoryPackOrder(0)] public required long RootId { get; init; }
    [MemoryPackOrder(1)] public required string RootPath { get; init; }
    [MemoryPackOrder(2)] public required long DirId { get; init; }
    [MemoryPackOrder(3)] public required DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [MemoryPackOrder(4)] public DateTimeOffset? LastScannedAt { get; init; }


    [MemoryPackOrder(5)] public string? VolumeId { get; init; }
    [MemoryPackOrder(6)] public string? VolumeLabel { get; init; }
    [MemoryPackOrder(7)] public string? DisplayName { get; init; }

    [MemoryPackOrder(8)] public bool? IsRotational { get; init; }

    [MemoryPackOrder(9)] public string? FileSystemType { get; init; }

    // to be updated on each scan (may change for removable media)
    [MemoryPackOrder(10)] public string? DevicePath { get; init; }
    [MemoryPackOrder(11)] public string? DeviceModel { get; init; }
    [MemoryPackOrder(12)] public string? VolumePath { get; init; }
    [MemoryPackOrder(13)] public bool IsDeleted { get; init; }
    [MemoryPackOrder(14)] public DateTimeOffset? DeletedAtUtc { get; init; }
}
