// DuplicateFileFinderLib/IO/WindowsVolumeInfoProvider.cs

using System.Runtime.Versioning;

namespace DuplicateFileFinderLib.IO;

[SupportedOSPlatform("windows")]
public sealed class WindowsVolumeInfoProvider : IVolumeInfoProvider
{
    public VolumeInfo GetVolumeInfoForPath(string rootPath)
    {
        throw new NotSupportedException("Not tested at all");
        
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentNullException(nameof(rootPath));

        var fullPath = Path.GetFullPath(rootPath);
        var root     = Path.GetPathRoot(fullPath)
                       ?? throw new InvalidOperationException($"Cannot determine drive for {fullPath}");

        var drive = new DriveInfo(root);

        var volumeId = $"{drive.Name}|{drive.VolumeLabel}|{drive.DriveFormat}";

        // For now, we don't try to distinguish SSD vs HDD
        bool? isRotational = null;

        return new VolumeInfo
        {
            VolumeId       = volumeId,
            IsRotational   = isRotational,
            FileSystemType = drive.DriveFormat,
            DevicePath     = drive.Name
        };
    }
}