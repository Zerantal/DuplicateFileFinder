// DuplicateFileFinderLib/IO/LinuxVolumeInfoProvider.cs

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuplicateFileFinderLib.IO;

[SupportedOSPlatform("linux")]
public sealed class LinuxVolumeInfoProvider : IVolumeInfoProvider
{
    public VolumeInfo? GetVolumeInfoForPath(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentNullException(nameof(rootPath));

        var fullPath = Path.GetFullPath(rootPath);

        // 1. Resolve the device node for this path (e.g. /dev/sdd1)
        var devicePath = ProbeDeviceWithFindmnt(fullPath)
                         ?? ProbeDeviceFromMountinfo(fullPath);

        if (string.IsNullOrEmpty(devicePath)) return null;

        // 2. Call lsblk once and build VolumeInfo from its JSON
        var lsblkJson = RunLsblkJson();
        if (string.IsNullOrWhiteSpace(lsblkJson)) return new VolumeInfo { DevicePath = devicePath };

        return BuildVolumeInfoFromLsblkJson(lsblkJson, devicePath);
    }

    // ---------- Device resolution (findmnt + /proc/*) ----------

    private static string? ProbeDeviceWithFindmnt(string path)
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
            psi.ArgumentList.Add("SOURCE");
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
            return string.IsNullOrEmpty(line) ? null : line;
        }
        catch
        {
            return null;
        }
    }

    private static string? ProbeDeviceFromMountinfo(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var bestMatchLen = -1;
            string? bestSource = null;

            // Prefer /proc/self/mountinfo, fall back to /proc/mounts
            var mountInfoPath = "/proc/self/mountinfo";
            if (!File.Exists(mountInfoPath))
                mountInfoPath = "/proc/mounts";

            foreach (var line in File.ReadLines(mountInfoPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // We want: <source> <target> ...  — /proc/mounts format is simpler;
                // /proc/self/mountinfo is more complex but source/target still appear early.
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                    continue;

                string source;
                string target;

                if (mountInfoPath.EndsWith("mountinfo", StringComparison.Ordinal))
                {
                    // mountinfo format:
                    //   30 24 8:17 / /mnt/external_vm_storage rw,relatime - ext4 /dev/sdd1 ...
                    // so:  source is field after '-', target is field 4
                    var dashIndex = Array.IndexOf(parts, "-");
                    if (dashIndex < 0 || dashIndex + 2 >= parts.Length || parts.Length <= 4)
                        continue;

                    target = parts[4]; // mountpoint (e.g. /mnt/external_vm_storage)
                    source = parts[dashIndex + 2]; // filesystem source (e.g. /dev/sdd1)
                }
                else
                {
                    // /proc/mounts format:
                    //   <source> <target> <fstype> <options> ...
                    source = parts[0];
                    target = parts[1];
                }

                if (!target.StartsWith("/", StringComparison.Ordinal))
                    continue;

                if (!full.StartsWith(target, StringComparison.Ordinal))
                    continue;

                var len = target.Length;
                if (len > bestMatchLen)
                {
                    bestMatchLen = len;
                    bestSource = source;
                }
            }

            return bestSource;
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
            psi.ArgumentList.Add("NAME,KNAME,TYPE,MODEL,ROTA,FSTYPE,LABEL,PARTLABEL,UUID,PARTUUID,WWN,SERIAL");

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
    internal static VolumeInfo? BuildVolumeInfoFromLsblkJson(string json, string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;
        if (string.IsNullOrWhiteSpace(json)) return new VolumeInfo { DevicePath = devicePath };

        var volInfo = new VolumeInfo { DevicePath = devicePath };

        LsblkRoot? root;
        try
        {
            root = JsonSerializer.Deserialize<LsblkRoot>(json);
        }
        catch
        {
            return volInfo;
        }

        if (root?.Blockdevices == null || root.Blockdevices.Count == 0) return volInfo;

        var devName = Path.GetFileName(devicePath);

        LsblkDevice? partitionNode = null;
        LsblkDevice? diskNode = null;

        void Dfs(LsblkDevice node, LsblkDevice? currentDisk)
        {
            var thisDisk = string.Equals(node.Type, "disk", StringComparison.OrdinalIgnoreCase)
                ? node
                : currentDisk;

            // Match either NAME or KNAME to the leaf device name
            if (!string.IsNullOrEmpty(devName) &&
                (string.Equals(node.Name, devName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.KName, devName, StringComparison.OrdinalIgnoreCase)))
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

        if (partitionNode is null) return volInfo;

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
            DeviceModel = hwModel
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