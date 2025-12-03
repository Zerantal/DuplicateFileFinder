using System.Diagnostics.CodeAnalysis;
using DuplicateFileFinderLib.IO.Platforms;
using Xunit;

namespace DuplicateFileFinderLibTests.IO.Platforms;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
public sealed class LinuxVolumeInfoProviderTests
{
  
    [Fact]
    public void BuildVolumeInfoFromLsblkJson_UsesWwnForDeviceId_AndPartuuidForVolumeId()
    {
        // Arrange: disk "sdd" with model + wwn, partition "sdd1" with ntfs3 and label
        var json = """
        {
          "blockdevices": [
            {
              "name": "sdd",
              "kname": "sdd",
              "type": "disk",
              "model": "Samsung Portable SSD T7",
              "rota": false,
              "label": "DISK_LABEL",
              "wwn": "0x500123456789abcd",
              "serial": "S123456789",
              "children": [
                {
                  "name": "sdd1",
                  "kname": "sdd1",
                  "type": "part",
                  "fstype": "ntfs",
                  "label": "FS_LABEL",
                  "partlabel": "PART1",
                  "uuid": "1111-2222",
                  "partuuid": "3333-4444"
                }
              ]
            }
          ]
        }
        """;

        var devicePath = "/dev/sdd1";

        var info = LinuxVolumeInfoProvider.BuildVolumeInfoFromLsblkJson(json, devicePath)!;

        Assert.Equal("partuuid:3333-4444", info.VolumeId);      // PARTUUID wins for VolumeId
        Assert.Equal("wwn:0x500123456789abcd", info.DeviceId);  // WWN wins for DeviceId
        Assert.Equal("PART1", info.Label);                      // PARTLABEL wins for Label
        Assert.Equal("ntfs", info.FileSystemType); // ntfs3 -> ntfs
        Assert.Equal(devicePath, info.DevicePath);
        Assert.Equal("Samsung Portable SSD T7", info.DeviceModel);
        Assert.False(info.IsRotational ?? true);
    }

    [Fact]
    public void BuildVolumeInfoFromLsblkJson_UsesSerialWhenNoWwn()
    {
        var json = """
        {
          "blockdevices": [
            {
              "name": "sdc",
              "type": "disk",
              "model": "Generic USB Disk",
              "rota": true,
              "serial": "USB123456",
              "children": [
                {
                  "name": "sdc1",
                  "type": "part",
                  "fstype": "ext4",
                  "uuid": "aaaa-bbbb"
                }
              ]
            }
          ]
        }
        """;

        var devicePath = "/dev/sdc1";

        var info = LinuxVolumeInfoProvider.BuildVolumeInfoFromLsblkJson(json, devicePath)!;

        Assert.Equal("serial:USB123456", info.DeviceId);   // no WWN -> serial
        Assert.Equal("uuid:aaaa-bbbb", info.VolumeId);     // no PARTUUID -> UUID
        Assert.Equal("ext4", info.FileSystemType);
        Assert.Equal(devicePath, info.DevicePath);
        Assert.Equal("Generic USB Disk", info.DeviceModel);
        Assert.True(info.IsRotational ?? false);
    }

    [Fact]
    public void BuildVolumeInfoFromLsblkJson_FallsBackWhenNoIds()
    {
        var json = """
        {
          "blockdevices": [
            {
              "name": "sdb",
              "type": "disk",
              "model": "Some Disk",
              "rota": false,
              "children": [
                {
                  "name": "sdb1",
                  "type": "part",
                  "fstype": "xfs"
                }
              ]
            }
          ]
        }
        """;

        var devicePath = "/dev/sdb1";

        var info = LinuxVolumeInfoProvider.BuildVolumeInfoFromLsblkJson(json, devicePath)!;

        Assert.Null(info.DeviceId);          // no WWN or Serial
        Assert.Null(info.VolumeId);          // no PARTUUID or UUID
        Assert.Equal("xfs", info.FileSystemType);
        Assert.Equal(devicePath, info.DevicePath);
        Assert.Equal("Some Disk", info.DeviceModel);
        Assert.False(info.IsRotational ?? true);
    }

    [Fact]
    public void BuildVolumeInfoFromLsblkJson_UnknownDevice_ReturnsDevicePathOnly()
    {
        var json = """
        {
          "blockdevices": [
            {
              "name": "sda",
              "type": "disk",
              "model": "Internal Disk",
              "rota": true
            }
          ]
        }
        """;

        var devicePath = "/dev/sdz1"; // not present

        var info = LinuxVolumeInfoProvider.BuildVolumeInfoFromLsblkJson(json, devicePath)!;

        Assert.Null(info.DeviceId);
        Assert.Null(info.VolumeId);
        Assert.Null(info.FileSystemType);
        Assert.Null(info.Label);
        Assert.Null(info.DeviceModel);
        Assert.Null(info.IsRotational);
        Assert.Equal(devicePath, info.DevicePath);
    }

    [Fact]
    public void BuildVolumeInfoFromLsblkJson_BadJson_YieldsDevicePathOnly()
    {
        var json = "{ not valid json";
        var devicePath = "/dev/sdd1";

        var info = LinuxVolumeInfoProvider.BuildVolumeInfoFromLsblkJson(json, devicePath)!;

        Assert.Equal(devicePath, info.DevicePath);
        Assert.Null(info.DeviceId);
        Assert.Null(info.VolumeId);
        Assert.Null(info.FileSystemType);
        Assert.Null(info.Label);
        Assert.Null(info.DeviceModel);
        Assert.Null(info.IsRotational);
    }
}
