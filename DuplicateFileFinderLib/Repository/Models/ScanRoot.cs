// DuplicateFileFinderLib/Repository/Models/ScanRoot.cs

using MemoryPack;

namespace DuplicateFileFinderLib.Repository.Models;

[MemoryPackable]
public partial record ScanRoot
{
    [MemoryPackOrder(0)] public required Guid Id { get; init; }

    /// <summary>
    /// Canonical logical root path at the time the root was created.
    /// Used for UI and root matching. Stored in platform-agnostic form
    /// (e.g. via PathUtils.NormalizePath).
    /// </summary>
    [MemoryPackOrder(1)] public required string RootPath { get; init; }

    /// <summary>
    /// The DirRecord.Id that corresponds to this root in the repo's directory tree.
    /// </summary>
    [MemoryPackOrder(2)] public required Guid DirId { get; init; }

    /// <summary>
    /// Last known VolumeId for this root. Updated at the end (or start) of each scan.
    /// </summary>
    [MemoryPackOrder(3)] public string? LastVolumeId { get; init; } = null;

    [MemoryPackOrder(4)] public string? LastVolumeDisplayName { get; init; } = null;

    [MemoryPackOrder(5)] public bool? LastIsRotational { get; init; } = null;

    [MemoryPackOrder(6)] public string? LastVolumeFileSystemType { get; init; } = null;

    [MemoryPackOrder(7)] public string? LastVolumeDevicePath { get; init; } = null;

    [MemoryPackOrder(8)] public required DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    [MemoryPackOrder(9)] public DateTimeOffset? LastScannedAt { get; init; } = null; // should probably reference GUID of last scanrun?
}