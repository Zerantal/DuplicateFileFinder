using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class DirRecordTests
{
    [Fact]
    public void DirRecord_MemoryPackRoundTrip_PreservesAllFields()
    {
        long id = 99;
        long parentId = 66;

        var original = new DirRecord
        {
            DirId = id,
            ParentDirId = parentId,
            Name = "subdir",
            LastSeenScanSequence = 123,
            Status = ScanEntryStatus.Error,
            ErrorMessage = "oops"
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var roundTripped = MemoryPackSerializer.Deserialize<DirRecord>(bytes)!;

        Assert.Equal(original.DirId, roundTripped.DirId);
        Assert.Equal(original.ParentDirId, roundTripped.ParentDirId);
        Assert.Equal(original.Name, roundTripped.Name);
        Assert.Equal(original.LastSeenScanSequence, roundTripped.LastSeenScanSequence);
        Assert.Equal(original.Status, roundTripped.Status);
        Assert.Equal(original.ErrorMessage, roundTripped.ErrorMessage);
    }
}