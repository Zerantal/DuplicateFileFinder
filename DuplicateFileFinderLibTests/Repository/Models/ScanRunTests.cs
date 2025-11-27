using System;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class ScanRunTests
{
    [Fact]
    public void ScanRun_MemoryPackRoundTrip_PreservesAllFields()
    {
        var original = new ScanRun
        {
            ScanSequence = 42,
            RootPath = "/some/root",
            StartedAt = new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2024, 2, 3, 5, 6, 7, TimeSpan.Zero),
            Status = ScanRunStatus.Completed,
            ErrorMessage = "none",
            Mode = ScanMode.Full,
            ScanRootId = Guid.NewGuid(),
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var roundTripped = MemoryPackSerializer.Deserialize<ScanRun>(bytes)!;

        Assert.Equal(original.ScanSequence, roundTripped.ScanSequence);
        Assert.Equal(original.RootPath, roundTripped.RootPath);
        Assert.Equal(original.StartedAt, roundTripped.StartedAt);
        Assert.Equal(original.FinishedAt, roundTripped.FinishedAt);
        Assert.Equal(original.Status, roundTripped.Status);
        Assert.Equal(original.ErrorMessage, roundTripped.ErrorMessage);
        Assert.Equal(original.Mode, roundTripped.Mode);
        Assert.Equal(original.ScanRootId, roundTripped.ScanRootId);
    }

    [Fact]
    public void ScanRunStatus_Values_AreStable()
    {
        Assert.Equal(0, (byte)ScanRunStatus.InProgress);
        Assert.Equal(1, (byte)ScanRunStatus.Completed);
        Assert.Equal(2, (byte)ScanRunStatus.Failed);
        Assert.Equal(3, (byte)ScanRunStatus.Cancelled);
        
        Assert.Equal(0, (byte)ScanMode.Full);
        Assert.Equal(1, (byte)ScanMode.Quick);
        
    }
}