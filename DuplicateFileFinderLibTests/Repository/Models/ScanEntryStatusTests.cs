using DuplicateFileFinderLib.Repository.Models;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class ScanEntryStatusTests
{
    [Fact]
    public void ScanEntryStatus_NoneHasNoFlags()
    {
        var status = ScanEntryStatus.None;

        Assert.False(status.HasFlag(ScanEntryStatus.Enumerated));
        Assert.False(status.HasFlag(ScanEntryStatus.Hashed));
        Assert.False(status.HasFlag(ScanEntryStatus.Error));
        Assert.False(status.HasFlag(ScanEntryStatus.SkippedByFilter));
        Assert.False(status.HasFlag(ScanEntryStatus.Deleted));
    }

    [Fact]
    public void ScanEntryStatus_ComposingFlagsProducesExpectedBits()
    {
        var combined = ScanEntryStatus.Enumerated | ScanEntryStatus.Hashed;

        Assert.Equal((byte)0b00000011, (byte)combined);
        Assert.True(combined.HasFlag(ScanEntryStatus.Enumerated));
        Assert.True(combined.HasFlag(ScanEntryStatus.Hashed));
    }

    [Fact]
    public void ScanEntryStatus_DeletedFlagDistinct()
    {
        Assert.Equal((byte)0b00010000, (byte)ScanEntryStatus.Deleted);
    }
}