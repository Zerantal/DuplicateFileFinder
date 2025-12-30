using System;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;
using Xunit;
using DuplicateFileFinderLibTests.TestUtils;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class DirRecordV2Tests
{
    [Fact]
    public void DirRecordV2_MemoryPack_RoundTrips_AllFields()
    {
        var original = new DirRecordV2
        {
            DirId = 123,
            ParentDirId = 45,
            NameStrIdx = 7,
            LastSeenScanSequence = 999,
            Status = ScanEntryStatus.Error,
            ErrorMessageStrIdx = 88,
            ModifiedTicks = DateTimeOffset.UtcNow.UtcTicks,
            CreatedTicks = DateTimeOffset.UtcNow.AddDays(-1).UtcTicks
        };

        var clone = MemoryPackUtils.RoundTrip(original);

        AssertDirEqual(original, clone);
    }

    [Fact]
    public void DirRecordV2_Array_MemoryPack_RoundTrips_AllElements()
    {
        var originals = new[]
        {
            new DirRecordV2
            {
                DirId = 1, ParentDirId = -1, NameStrIdx = 0, LastSeenScanSequence = 10,
                Status = ScanEntryStatus.Hashed, ErrorMessageStrIdx = -1, ModifiedTicks = 0, CreatedTicks = 0
            },
            new DirRecordV2
            {
                DirId = 2, ParentDirId = 1, NameStrIdx = 1, LastSeenScanSequence = 11,
                Status = ScanEntryStatus.Error, ErrorMessageStrIdx = 2, ModifiedTicks = 123, CreatedTicks = 456
            }
        };

        var clones = MemoryPackUtils.RoundTrip(originals);

        Assert.Equal(originals.Length, clones.Length);
        for (int i = 0; i < originals.Length; i++)
            AssertDirEqual(originals[i], clones[i]);
    }

    // NOTE: DirRecordV2 implements IEquatable based on Id only.
    // These assertions verify full field roundtrip (stronger than Equals()).
    private static void AssertDirEqual(in DirRecordV2 a, in DirRecordV2 b)
    {
        Assert.Equal(a.DirId, b.DirId);
        Assert.Equal(a.ParentDirId, b.ParentDirId);
        Assert.Equal(a.NameStrIdx, b.NameStrIdx);
        Assert.Equal(a.LastSeenScanSequence, b.LastSeenScanSequence);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.ErrorMessageStrIdx, b.ErrorMessageStrIdx);
        Assert.Equal(a.ModifiedTicks, b.ModifiedTicks);
        Assert.Equal(a.CreatedTicks, b.CreatedTicks);
    }


}