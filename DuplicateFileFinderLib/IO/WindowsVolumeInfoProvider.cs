// DuplicateFileFinderLib/IO/WindowsVolumeInfoProvider.cs

using System.Runtime.Versioning;

namespace DuplicateFileFinderLib.IO;

[SupportedOSPlatform("windows")]
public sealed class WindowsVolumeInfoProvider : IVolumeInfoProvider
{
    public VolumeInfo GetVolumeInfoForPath(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentNullException(nameof(rootPath));

        var fullPath = Path.GetFullPath(rootPath);
        var root     = Path.GetPathRoot(fullPath)
                       ?? throw new InvalidOperationException($"Cannot determine drive for {fullPath}");

        var drive = new DriveInfo(root);

        var volumeId = $"{drive.Name}|{drive.VolumeLabel}|{drive.DriveFormat}";
        var display  = string.IsNullOrWhiteSpace(drive.VolumeLabel)
            ? drive.Name.TrimEnd('\\')
            : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";

        // For now, we don't try to distinguish SSD vs HDD
        bool? isRotational = null;

        return new VolumeInfo
        {
            VolumeId       = volumeId,
            DisplayName    = display,
            IsRotational   = isRotational,
            FileSystemType = drive.DriveFormat,
            DevicePath     = drive.Name
        };
    }
}