using System.Diagnostics.CodeAnalysis;
using DuplicateFileFinderLib.IO;
using Xunit;

namespace DuplicateFileFinderLibTests.IO;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
public sealed class LinuxVolumeInfoProviderTests
{
  
    [Fact]
    public void BuildVolumeInfoFromLsblkJson_PicksWwnAsVolumeId_AndDiskModel()
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
              "wwn": "0x500123456789abcd",
              "serial": "S123456789",
              "children": [
                {
                  "name": "sdd1",
                  "kname": "sdd1",
                  "type": "part",
                  "fstype": "ntfs",
                  "partlabel": "PORTABLE",
                  "uuid": "1111-2222",
                  "partuuid": "3333-4444"
                }
              ]
            }
          ]
        }
        """;

        var devicePath = "/dev/sdd1";

        // Act
        var info = LinuxVolumeInfoProvider.BuildVolumeInfoFromLsblkJson(json, devicePath);

        // Assert
        Assert.NotNull(info);
        Assert.Equal("wwn:0x500123456789abcd", info.DeviceId);
        Assert.Equal("partuuid:3333-4444", info.VolumeId);
        Assert.Equal("PORTABLE", info.Label);
        Assert.Equal("ntfs", info.FileSystemType); // ntfs3 -> ntfs
        Assert.Equal(devicePath, info.DevicePath);
        Assert.Equal("Samsung Portable SSD T7", info.DeviceModel);
        Assert.False(info.IsRotational ?? true); // should be false
    }

    [Fact]
    public void BuildVolumeInfoFromLsblkJson_FallsBackToSerialWhenNoWwn()
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
                  "label": "",
                  "uuid": "aaaa-bbbb",
                  "partuuid": "cccc-dddd"
                }
              ]
            }
          ]
        }
        """;

        var devicePath = "/dev/sdc1";

        var info = LinuxVolumeInfoProvider.BuildVolumeInfoFromLsblkJson(json, devicePath);

        Assert.NotNull(info);
        Assert.Equal("serial:USB123456", info.DeviceId);
        Assert.True(string.IsNullOrEmpty(info.Label));
        Assert.Equal("ext4", info.FileSystemType);
        Assert.Equal(devicePath, info.DevicePath);
        Assert.Equal("Generic USB Disk", info.DeviceModel);
        Assert.True(info.IsRotational ?? false);
    }

    [Fact]
    public void BuildVolumeInfoFromLsblkJson_FallsBackUuidFromPartuuid()
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
                  "fstype": "xfs",
                  "uuid": "uu-11-22",
                  "partuuid": "pp-33-44"
                },
                {
                  "name": "sdb2",
                  "type": "part",
                  "fstype": "xfs",
                  "uuid": "uu-55-66"
                }
              ]
            }
          ]
        }
        """;

        var dev1 = "/dev/sdb1";
        var dev2 = "/dev/sdb2";

        var info1 = LinuxVolumeInfoProvider.BuildVolumeInfoFromLsblkJson(json, dev1);
        var info2 = LinuxVolumeInfoProvider.BuildVolumeInfoFromLsblkJson(json, dev2);

        // sdb1: partuuid present
        Assert.NotNull(info1);
        Assert.Equal("partuuid:pp-33-44", info1.VolumeId);
        Assert.Equal("xfs", info1.FileSystemType);
        Assert.Equal(dev1, info1.DevicePath);

        // sdb2: no partuuid, uuid present
        Assert.NotNull(info2);
        Assert.Equal("uuid:uu-55-66", info2.VolumeId);
        Assert.Equal("xfs", info2.FileSystemType);
        Assert.Equal(dev2, info2.DevicePath);
    }

    [Fact]
    public void BuildVolumeInfoFromLsblkJson_UnknownDevice_FallsBackToDevicePath()
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

        var devicePath = "/dev/sdz1"; // not present in JSON

        var info = LinuxVolumeInfoProvider.BuildVolumeInfoFromLsblkJson(json, devicePath);

        Assert.NotNull(info);
        Assert.Null(info.VolumeId);
        Assert.Null(info.DeviceId);
        Assert.Null(info.FileSystemType);
        Assert.Null(info.Label);
        Assert.Null(info.IsRotational);
        Assert.Null(info.DeviceModel);
    }

    [Fact]
    public void BuildVolumeInfoFromLsblkJson_BadJson_YieldsUnknown()
    {
        var json = "{ this is not valid json";
        var devicePath = "/dev/sdd1";

        var info = LinuxVolumeInfoProvider.BuildVolumeInfoFromLsblkJson(json, devicePath);

        Assert.NotNull(info);
        Assert.Null(info.VolumeId);
        Assert.Equal(devicePath, info.DevicePath);
    }
}
