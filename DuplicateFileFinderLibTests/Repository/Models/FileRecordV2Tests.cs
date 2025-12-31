using System;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class FileRecordV2Tests
{
    [Fact]
    public void FileRecordV2_MemoryPack_RoundTrips_AllFields()
    {
        var original = new FileRecordV2
        {
            FileId = 555,
            DirId = 123,
            NameStrIdx = 9,
            Size = 42_4242,
            Hash = HashKey.NotComputed,
            ModifiedTicks = DateTimeOffset.UtcNow.UtcTicks,
            CreatedTicks = DateTimeOffset.UtcNow.AddHours(-3).UtcTicks,
            LastSeenScanSequence = 1001,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessageStrIdx = -1
        };

        var clone = MemoryPackUtils.RoundTrip(original);

        AssertFileEqual(original, clone);
    }

    [Fact]
    public void FileRecordV2_Array_MemoryPack_RoundTrips_AllElements()
    {
        var originals = new[]
        {
            new FileRecordV2
            {
                FileId = 10, DirId = 2, NameStrIdx = 5, Size = 123, Hash = HashKey.NotComputed,
                ModifiedTicks = 1, CreatedTicks = 2, LastSeenScanSequence = 3, Status = ScanEntryStatus.None,
                ErrorMessageStrIdx = -1
            },
            new FileRecordV2
            {
                FileId = 11, DirId = 2, NameStrIdx = 6, Size = 456, Hash = HashKey.NotComputed,
                ModifiedTicks = 10, CreatedTicks = 20, LastSeenScanSequence = 30, Status = ScanEntryStatus.Error,
                ErrorMessageStrIdx = 99
            }
        };

        var clones = MemoryPackUtils.RoundTrip(originals);

        Assert.Equal(originals.Length, clones.Length);
        for (int i = 0; i < originals.Length; i++)
            AssertFileEqual(originals[i], clones[i]);
    }

    // NOTE: FileRecordV2 implement IEquatable based on Id only.
    // These assertions verify full field roundtrip (stronger than Equals()).
    private static void AssertFileEqual(in FileRecordV2 a, in FileRecordV2 b)
    {
        Assert.Equal(a.FileId, b.FileId);
        Assert.Equal(a.DirId, b.DirId);
        Assert.Equal(a.NameStrIdx, b.NameStrIdx);
        Assert.Equal(a.Size, b.Size);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.ModifiedTicks, b.ModifiedTicks);
        Assert.Equal(a.CreatedTicks, b.CreatedTicks);
        Assert.Equal(a.LastSeenScanSequence, b.LastSeenScanSequence);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.ErrorMessageStrIdx, b.ErrorMessageStrIdx);
    }
}