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
            DirId = id,
            ParentId = parentId,
            Name = "subdir",
            LastSeenSequence = 123,
            Status = ScanEntryStatus.Error,
            ErrorMessage = "oops"
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var roundTripped = MemoryPackSerializer.Deserialize<DirRecord>(bytes)!;

        Assert.Equal(original.DirId, roundTripped.DirId);
        Assert.Equal(original.ParentId, roundTripped.ParentId);
        Assert.Equal(original.Name, roundTripped.Name);
        Assert.Equal(original.LastSeenSequence, roundTripped.LastSeenSequence);
        Assert.Equal(original.Status, roundTripped.Status);
        Assert.Equal(original.ErrorMessage, roundTripped.ErrorMessage);
    }
}