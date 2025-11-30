using System;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class RepoMetaTests
{
    [Fact]
    public void RepoMeta_MemoryPackRoundTrip_PreservesAllFields()
    {
        var repoId = Guid.NewGuid();
        var original = new RepoMeta
        {
            SchemaVersion = 4,
            Generation = 2,
            NextLogSequence = 10,
            LastSnapshottedLogSequence = 8,
            LastCompaction = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.Zero),
            RepoId = repoId,
            RepoPath = "/repo/path",
            RepoHostName = "host-name",
            NextRunId = 20
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var roundTripped = MemoryPackSerializer.Deserialize<RepoMeta>(bytes)!;

        Assert.Equal(original.SchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal(original.Generation, roundTripped.Generation);
        Assert.Equal(original.NextLogSequence, roundTripped.NextLogSequence);
        Assert.Equal(original.LastSnapshottedLogSequence, roundTripped.LastSnapshottedLogSequence);
        Assert.Equal(original.LastCompaction, roundTripped.LastCompaction);
        Assert.Equal(original.RepoId, roundTripped.RepoId);
        Assert.Equal(original.RepoPath, roundTripped.RepoPath);
        Assert.Equal(original.RepoHostName, roundTripped.RepoHostName);
        Assert.Equal(original.NextRunId, roundTripped.NextRunId);
    }
}