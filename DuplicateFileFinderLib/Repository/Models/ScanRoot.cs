// DuplicateFileFinderLib/Repository/Models/ScanRoot.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record ScanRoot
{
    [MemoryPackOrder(0)] public required Guid Id                   { get; init; }

    /// <summary>
    /// Canonical logical root path at the time the root was created.
    /// Used for UI and root matching. Stored in platform-agnostic form
    /// (e.g. via PathUtils.NormalizePath).
    /// </summary>
    [MemoryPackOrder(1)] public required string RootPath          { get; init; }

    /// <summary>
    /// The DirRecord.FileId that corresponds to this root in the repo's directory tree.
    /// </summary>
    [MemoryPackOrder(2)] public required Guid DirId               { get; init; }
    
    [MemoryPackOrder(9)] public required DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [MemoryPackOrder(10)] public DateTimeOffset? LastScannedAt    { get; init; }
    
    
    [MemoryPackOrder(3)] public string? VolumeId                  { get; init; }
    
    [MemoryPackOrder(4)] public string? VolumeLabel               { get; init; }
    
    [MemoryPackOrder(5)] public string? DisplayName               { get; init; }

    [MemoryPackOrder(6)] public bool? IsRotational                { get; init; }

    [MemoryPackOrder(7)] public string? FileSystemType            { get; init; }
    
    
    // to be updated on each scan (may change for removable media)
    [MemoryPackOrder(8)] public string? DevicePath                { get; init; }
    [MemoryPackOrder(11)] public string? DeviceModel               { get; init; }
}