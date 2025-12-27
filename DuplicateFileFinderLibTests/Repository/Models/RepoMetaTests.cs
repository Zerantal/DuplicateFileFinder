using System;
using DuplicateFileFinderLib.Repository.Storage.Models;
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
            RepoId = repoId,
            RepoPath = "/repo/path",
            RepoHostName = "host-name",
            NextScanSequence = 20
        };

        var bytes = MemoryPackSerializer.Serialize(original);
        var roundTripped = MemoryPackSerializer.Deserialize<RepoMeta>(bytes)!;

        Assert.Equal(original.SchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal(original.Generation, roundTripped.Generation);
        Assert.Equal(original.RepoId, roundTripped.RepoId);
        Assert.Equal(original.RepoPath, roundTripped.RepoPath);
        Assert.Equal(original.RepoHostName, roundTripped.RepoHostName);
        Assert.Equal(original.NextScanSequence, roundTripped.NextScanSequence);
    }
}