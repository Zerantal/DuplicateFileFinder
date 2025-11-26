// DuplicateFileFinderLib/IO/VolumeInfo.cs

namespace DuplicateFileFinderLib.IO;

public sealed class VolumeInfo
{
    /// <summary>
    /// Stable identifier for the volume. Prefer a UUID if available; otherwise use
    /// the device path or a composite (device + fs type).
    /// </summary>
    public string VolumeId { get; init; } = "unknown";

    /// <summary>
    /// Human-friendly label for UI: e.g. "Samsung T7 (/dev/sdb1)" or "Data (D:)".
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// True if the underlying storage is rotational (HDD). False if SSD. Null if unknown.
    /// </summary>
    public bool? IsRotational { get; init; }

    /// <summary>
    /// Filesystem type, e.g. "ext4", "ntfs", "xfs". Optional.
    /// </summary>
    public string? FileSystemType { get; init; }

    /// <summary>
    /// Underlying device path, e.g. "/dev/sda1" or "\\.\PhysicalDrive0".
    /// </summary>
    public string? DevicePath { get; init; }

    public override string ToString()
        => $"{DisplayName ?? VolumeId} (rotational={IsRotational?.ToString() ?? "?"}, fs={FileSystemType ?? "?"})";
}