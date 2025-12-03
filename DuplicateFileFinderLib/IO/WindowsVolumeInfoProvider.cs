// DuplicateFileFinderLib/IO/WindowsVolumeInfoProvider.cs

using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DuplicateFileFinderLib.IO;

// TODO: Testing / Validation

[SupportedOSPlatform("windows")]
public sealed class WindowsVolumeInfoProvider : IVolumeInfoProvider
{
    // ReSharper disable once ReturnTypeCanBeNotNullable
    public VolumeInfo? GetVolumeInfoForPath(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentNullException(nameof(rootPath));

        var fullPath = Path.GetFullPath(rootPath);

        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            throw new DirectoryNotFoundException($"Path does not exist: {fullPath}");

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            throw new InvalidOperationException($"Could not determine volume root for path '{fullPath}'.");

        var driveInfo = new DriveInfo(root);

        if (!driveInfo.IsReady)
        {
            // Drive exists but media not ready (no disk, etc.)
            return new VolumeInfo
            {
                DevicePath      = root,
                VolumeId        = null,
                DeviceId        = null,
                Label           = null,
                FileSystemType  = null,
                IsRotational    = null,
                DeviceModel     = null
            };
        }

        // Network drives can't be mapped to local physical devices.
        if (driveInfo.DriveType == DriveType.Network)
        {
            return new VolumeInfo
            {
                DevicePath      = root,
                VolumeId        = null,
                DeviceId        = null,
                Label           = SafeGetVolumeLabel(driveInfo),
                FileSystemType  = SafeGetDriveFormat(driveInfo),
                IsRotational    = null,
                DeviceModel     = null
            };
        }

        var volumeGuidPath = TryGetVolumeGuidPath(root); // e.g. "\\\\?\\Volume{GUID}\\"
        var devicePath     = volumeGuidPath ?? root;
        var volumeId       = volumeGuidPath;             // Treat the volume GUID as the VolumeId on Windows.

        var label          = SafeGetVolumeLabel(driveInfo);
        var fileSystemType = SafeGetDriveFormat(driveInfo);

        var (deviceId, deviceModel) = TryGetPhysicalDeviceInfo(root, driveInfo);

        return new VolumeInfo
        {
            DevicePath      = devicePath,
            VolumeId        = volumeId,
            DeviceId        = deviceId,        // e.g. "\\\\.\\PHYSICALDRIVE0"
            DeviceModel     = deviceModel,     // e.g. "Samsung SSD 980 ..."
            Label           = label,
            FileSystemType  = fileSystemType,
            IsRotational    = null            // Can be populated later via DeviceIoControl / MSFT_PhysicalDisk if desired.
        };
    }

    private static string? SafeGetVolumeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeGetDriveFormat(DriveInfo drive)
    {
        try
        {
            return drive.DriveFormat;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetVolumeGuidPath(string volumeRoot)
    {
        if (string.IsNullOrWhiteSpace(volumeRoot))
            return null;

        // volumeRoot is expected to be like "C:\\"
        var sb = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPoint(volumeRoot, sb, sb.Capacity))
            return null;

        var guidPath = sb.ToString();
        // Normalise: trim trailing backslash to be consistent.
        return guidPath.TrimEnd('\\');
    }

    /// <summary>
    /// Maps a logical drive (e.g. "C:\") to the underlying physical disk, returning DeviceID and Model.
    /// </summary>
    private static (string? deviceId, string? model) TryGetPhysicalDeviceInfo(string driveRoot, DriveInfo driveInfo)
    {
        // Only try to map fixed/removable drives; others (CD-ROM, RAM, etc.) can be skipped.
        if (driveInfo.DriveType != DriveType.Fixed &&
            driveInfo.DriveType != DriveType.Removable)
        {
            return (null, null);
        }

        try
        {
            // Convert "C:\\" -> "C:"
            var logicalDeviceId = driveRoot.TrimEnd('\\');

            using var logicalDisk = new ManagementObject($"Win32_LogicalDisk.DeviceID='{logicalDeviceId}'");
            logicalDisk.Get(); // ensure it's loaded

            var partitions = logicalDisk.GetRelated("Win32_DiskPartition");
            foreach (var o in partitions)
            {
                var partition = (ManagementObject)o;
                var drives = partition.GetRelated("Win32_DiskDrive");
                foreach (var managementBaseObject in drives)
                {
                    var drive = (ManagementObject)managementBaseObject;
                    var devId = drive["DeviceID"] as string; // e.g. "\\\\.\\PHYSICALDRIVE0"
                    var model = drive["Model"] as string;
                    return (devId, model);
                }
            }
        }
        catch (ManagementException)
        {
            // Ignore and fall through to nulls.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore and fall through to nulls.
        }

        return (null, null);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint,
        [Out] StringBuilder lpszVolumeName,
        int cchBufferLength);
}