// DuplicateFileFinderLib/IO/LinuxVolumeInfoProvider.cs

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuplicateFileFinderLib.IO.Platforms;

[SupportedOSPlatform("linux")]
public sealed class LinuxVolumeInfoProvider : IVolumeInfoProvider
{
    public VolumeInfo? GetVolumeInfoForPath(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentNullException(nameof(rootPath));

        var fullPath = Path.GetFullPath(rootPath);

        // 1. Resolve the device node for this path (e.g. /dev/sdd1)
        var pathProbe = ProbeDeviceWithFindmnt(fullPath);

        if (!pathProbe.HasValue)
            return null;
        var devicePath = pathProbe.Value.devicePath;
        var volumePath = pathProbe.Value.volumePath;
        if (string.IsNullOrEmpty(devicePath) || string.IsNullOrEmpty(volumePath))
            return null;

        devicePath = NormalizeDevicePath(devicePath);

        // 2. Call lsblk once and build VolumeInfo from its JSON
        var lsblkJson = RunLsblkJson();
        if (string.IsNullOrWhiteSpace(lsblkJson))
            return new VolumeInfo { DevicePath = devicePath, VolumePath = volumePath };

        return BuildVolumeInfoFromLsblkJson(lsblkJson, devicePath, volumePath);
    }

    // ---------- Device resolution - findmnt ----------

    private static string NormalizeDevicePath(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return devicePath;

        var trimmed = devicePath.Trim();

        // Handle btrfs-style "/dev/mapper/root[/@home]" etc.
        var bracketIndex = trimmed.IndexOf('[');
        if (bracketIndex > 0 && trimmed.EndsWith("]", StringComparison.Ordinal))
            trimmed = trimmed.Substring(0, bracketIndex);

        return trimmed;
    }


    private static (string? devicePath, string? volumePath)? ProbeDeviceWithFindmnt(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "findmnt",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-no");
            psi.ArgumentList.Add("SOURCE,TARGET");
            psi.ArgumentList.Add("--target");
            psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi);
            if (proc == null)
                return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            if (proc.ExitCode != 0)
                return null;

            var line = output.Trim();
            if (string.IsNullOrEmpty(line))
                return null;
            var strList = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string? devicePath = null;
            string? volumePath = null;
            if (strList.Length > 0)
                devicePath = strList[0];
            if (strList.Length > 1)
                volumePath = strList[1];

            return (devicePath, volumePath);
        }
        catch
        {
            return null;
        }
    }

    // ---------- lsblk JSON & parsing ----------

    private static string? RunLsblkJson()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "lsblk",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // lsblk -J -o NAME,KNAME,TYPE,MODEL,ROTA,FSTYPE,LABEL,PARTLABEL,UUID,PARTUUID,WWN,SERIAL
            psi.ArgumentList.Add("-J");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("NAME,KNAME,PATH,TYPE,MODEL,ROTA,FSTYPE,LABEL,PARTLABEL,UUID,PARTUUID,WWN,SERIAL");

            using var proc = Process.Start(psi);
            if (proc == null)
                return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            if (proc.ExitCode != 0)
                return null;

            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Core logic: given lsblk JSON and a device path (/dev/sdd1),
    ///     construct a VolumeInfo with identity, model, fs info, rotational flag.
    ///     Exposed as internal for unit tests.
    /// </summary>
    internal static VolumeInfo? BuildVolumeInfoFromLsblkJson(string json, string devicePath, string volumePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return null;
        if (string.IsNullOrWhiteSpace(json))
            return new VolumeInfo { DevicePath = devicePath, VolumePath = volumePath };

        var volInfo = new VolumeInfo { DevicePath = devicePath, VolumePath = volumePath };

        LsblkRoot? root;
        try
        {
            root = JsonSerializer.Deserialize<LsblkRoot>(json);
        }
        catch
        {
            return volInfo;
        }

        if (root?.Blockdevices == null || root.Blockdevices.Count == 0)
            return volInfo;

        var devName = Path.GetFileName(devicePath);

        LsblkDevice? partitionNode = null;
        LsblkDevice? diskNode = null;

        void Dfs(LsblkDevice node, LsblkDevice? currentDisk)
        {
            var thisDisk = string.Equals(node.Type, "disk", StringComparison.OrdinalIgnoreCase)
                ? node
                : currentDisk;

            var pathMatches = string.Equals(node.Path, devicePath, StringComparison.OrdinalIgnoreCase);
            var nameMatches = !string.IsNullOrEmpty(devName) &&
                              (string.Equals(node.Name, devName, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(node.KName, devName, StringComparison.OrdinalIgnoreCase));

            if (pathMatches || nameMatches)
            {
                partitionNode ??= node;
                diskNode ??= thisDisk;
            }

            if (node.Children is null)
                return;

            foreach (var child in node.Children)
                Dfs(child, thisDisk);
        }

        foreach (var top in root.Blockdevices)
        {
            var asDisk = top.Type?.Equals("disk", StringComparison.OrdinalIgnoreCase) == true ? top : null;
            Dfs(top, asDisk);
        }

        if (partitionNode is null)
            return volInfo;

        // Filesystem-level info (partition)
        var label = partitionNode.PartitionLabel ?? diskNode?.Label;
        var uuid = partitionNode.Uuid;
        var partUuid = partitionNode.Partuuid;
        var fsType = partitionNode.Fstype;

        // Hardware-level info (disk node if available)
        var hwModel = diskNode?.Model ?? partitionNode.Model;
        var rota = diskNode?.Rota ?? partitionNode.Rota;
        var wwn = diskNode?.Wwn ?? partitionNode.Wwn;
        var serial = diskNode?.Serial ?? partitionNode.Serial;

        // DeviceId: WWN or Serial; null if neither is available
        string? deviceId = null;
        if (!string.IsNullOrWhiteSpace(wwn))
            deviceId = $"wwn:{wwn}";
        else if (!string.IsNullOrWhiteSpace(serial))
            deviceId = $"serial:{serial}";

        // VolumeId: PARTUUID or UUID; null if neither is available
        string? volumeId = null;
        if (!string.IsNullOrWhiteSpace(partUuid))
            volumeId = $"partuuid:{partUuid}";
        else if (!string.IsNullOrWhiteSpace(uuid))
            volumeId = $"uuid:{uuid}";

        return new VolumeInfo
        {
            VolumeId = volumeId,
            DeviceId = deviceId,
            Label = label,
            FileSystemType = fsType,
            DevicePath = devicePath,
            IsRotational = rota,
            DeviceModel = hwModel,
            VolumePath = volumePath
        };
    }

    private sealed class LsblkRoot
    {
        [JsonPropertyName("blockdevices")] public List<LsblkDevice>? Blockdevices { get; set; }
    }

    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Local")]
    private sealed class LsblkDevice
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("kname")] public string? KName { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("rota")] public bool? Rota { get; set; }
        [JsonPropertyName("fstype")] public string? Fstype { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("partlabel")] public string? PartitionLabel { get; set; }
        [JsonPropertyName("uuid")] public string? Uuid { get; set; }
        [JsonPropertyName("partuuid")] public string? Partuuid { get; set; }
        [JsonPropertyName("wwn")] public string? Wwn { get; set; }
        [JsonPropertyName("serial")] public string? Serial { get; set; }

        // ReSharper disable once CollectionNeverUpdated.Local
        [JsonPropertyName("children")] public List<LsblkDevice>? Children { get; set; }
    }
}
