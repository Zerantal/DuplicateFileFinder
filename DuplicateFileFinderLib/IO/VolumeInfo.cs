// DuplicateFileFinderLib/IO/VolumeInfo.cs

namespace DuplicateFileFinderLib.IO;

public sealed class VolumeInfo
{
    public string? VolumeId { get; init; }
    public string? DeviceId { get; init; }
    public string? Label { get; init; }

    public bool? IsRotational { get; init; }

    public string? FileSystemType { get; init; }

    public required string DevicePath { get; init; }

    public string? DeviceModel { get; init; }

    public required string VolumePath { get; init; }
}
