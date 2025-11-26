// DuplicateFileFinderLib/IO/LinuxVolumeInfoProvider.cs

using System.Runtime.Versioning;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.IO;

[SupportedOSPlatform("linux")]
public sealed class LinuxVolumeInfoProvider : IVolumeInfoProvider
{
    public VolumeInfo GetVolumeInfoForPath(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentNullException(nameof(rootPath));

        var normalized = PathUtils.NormalizePath(rootPath);
        var mounts     = ReadProcMounts("/proc/mounts");

        // Find the longest mountpoint prefix that matches this path
        var best = mounts
            .Where(m => normalized.StartsWith(m.MountPoint, StringComparison.Ordinal))
            .OrderByDescending(m => m.MountPoint.Length)
            .FirstOrDefault();

        if (best is null)
        {
            return new VolumeInfo
            {
                VolumeId     = "unknown",
                DisplayName  = "Unknown volume",
                IsRotational = null
            };
        }

        var source = best.Source;   // e.g. /dev/sda1, /dev/nvme0n1p1, /dev/mapper/...
        var fsType = best.FsType;

        var deviceName = GetBlockDeviceNameFromSource(source);
        bool? isRotational = null;

        if (!string.IsNullOrEmpty(deviceName))
        {
            var rotationalPath = $"/sys/block/{deviceName}/queue/rotational";
            var line = TryReadFirstLine(rotationalPath)?.Trim();
            if (line == "0") isRotational = false;
            else if (line == "1") isRotational = true;
        }

        var volumeId = ResolveVolumeUuid(source) ?? source;

        var display = $"{source} ({fsType})";

        return new VolumeInfo
        {
            VolumeId      = volumeId,
            DisplayName   = display,
            IsRotational  = isRotational,
            FileSystemType = fsType,
            DevicePath    = source
        };
    }

    private sealed class MountEntry
    {
        public required string Source { get; init; }
        public required string MountPoint { get; init; }
        public required string FsType { get; init; }
    }

    private static List<MountEntry> ReadProcMounts(string path)
    {
        var result = new List<MountEntry>();

        if (!File.Exists(path))
            return result;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Format: <source> <target> <fstype> <options> ...
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            result.Add(new MountEntry
            {
                Source     = parts[0],
                MountPoint = parts[1],
                FsType     = parts[2]
            });
        }

        return result;
    }

    private static string? TryReadFirstLine(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var sr = new StreamReader(path);
            return sr.ReadLine();
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveVolumeUuid(string sourceDevice)
    {
        // Best-effort: scan /dev/disk/by-uuid and look for a symlink matching sourceDevice.
        try
        {
            const string byUuidDir = "/dev/disk/by-uuid";
            if (!Directory.Exists(byUuidDir))
                return null;

            foreach (var entry in Directory.EnumerateFileSystemEntries(byUuidDir))
            {
                try
                {
                    var target = Path.GetFullPath(PathUtils.NormalizePath(entry));
                    var real   = Path.GetFullPath(sourceDevice);
                    if (string.Equals(target, real, StringComparison.Ordinal))
                    {
                        var name = Path.GetFileName(entry);
                        return $"uuid:{name}";
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? GetBlockDeviceNameFromSource(string source)
    {
        // Very rough heuristic: /dev/sda1 -> sda, /dev/nvme0n1p1 -> nvme0n1
        try
        {
            var name = Path.GetFileName(source);
            if (string.IsNullOrEmpty(name))
                return null;

            // Strip partition suffixes
            // sda1 -> sda; nvme0n1p1 -> nvme0n1
            var i = name.Length - 1;
            while (i >= 0 && char.IsDigit(name[i])) i--;
            if (i >= 0 && name[i] == 'p')
                i--;

            return i <= 0 ? name : name[..(i + 1)];
        }
        catch
        {
            return null;
        }
    }
}
