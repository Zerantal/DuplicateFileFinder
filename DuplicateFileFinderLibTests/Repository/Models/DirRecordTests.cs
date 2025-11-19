using System;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class DirRecordTests
{
    [Fact]
    public void DirRecord_MemoryPackRoundTrip_PreservesAllFields()
    {
        var id = Guid.NewGuid();
        var parentId = Guid.NewGuid();

        var original = new DirRecord
        {
            Id = id,
            ParentId = parentId,
            Name = "subdir",
            LastSeenSequence = 123,
            Status = ScanEntryStatus.Enumerated | ScanEntryStatus.Error,
            ErrorMessage = "oops"
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var roundTripped = MemoryPackSerializer.Deserialize<DirRecord>(bytes)!;

        Assert.Equal(original.Id, roundTripped.Id);
        Assert.Equal(original.ParentId, roundTripped.ParentId);
        Assert.Equal(original.Name, roundTripped.Name);
        Assert.Equal(original.LastSeenSequence, roundTripped.LastSeenSequence);
        Assert.Equal(original.Status, roundTripped.Status);
        Assert.Equal(original.ErrorMessage, roundTripped.ErrorMessage);
    }

    [Fact]
    public void DirRecord_StatusFlags_WorkForDeletedAndError()
    {
        var status = ScanEntryStatus.Enumerated | ScanEntryStatus.Error | ScanEntryStatus.Deleted;

        Assert.True(status.HasFlag(ScanEntryStatus.Enumerated));
        Assert.True(status.HasFlag(ScanEntryStatus.Error));
        Assert.True(status.HasFlag(ScanEntryStatus.Deleted));

        Assert.False(status.HasFlag(ScanEntryStatus.Hashed));
        Assert.False(status.HasFlag(ScanEntryStatus.SkippedByFilter));
    }
}