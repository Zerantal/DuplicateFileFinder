// DuplicateFileFinderLibTests/Repository/RepoCompactionTests.cs
using System;
using System.IO;
using System.Linq;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository;

public sealed class RepoCompactionTests : IDisposable
{
    private readonly TempFsFixture _fs;

    public RepoCompactionTests()
    {
        _fs = new TempFsFixture("dff_repo_compact");
    }

    [Fact]
    public void CompactIfNeeded_WritesSnapshot_And_PrunesOldDeltas()
    {
        var repo = Repo.Open(_fs.Root);

        // create several small deltas
        var d = new DirRecord(Guid.NewGuid(), null, "root");
        repo.CommitDelta(new RepoDelta(new(), new() { d }));

        for (int i = 0; i < 6; i++)
        {
            var f = new FileRecord(Guid.NewGuid(), d.Id, $"f{i}.bin", i, [(byte)i], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
            repo.CommitDelta(new RepoDelta(new() { f }, new()));
        }

        var policy = new RepoCompactionPolicy
        {
            RatioThreshold = 1.0, // low bar to trigger
            MinLogBytes = 0,
            MinDeltaCount = 2
        };

        repo.CompactIfNeeded(policy);

        // snapshot exists
        Assert.True(File.Exists(Path.Combine(_fs.Root, "snapshot.bin")));

        // deltas at or below LastSnapshottedSequence should be gone
        var logFiles = Directory.Exists(Path.Combine(_fs.Root, "log"))
            ? Directory.GetFiles(Path.Combine(_fs.Root, "log"), "*.delta")
            : Array.Empty<string>();

        // some or all old deltas pruned, but repo state intact
        Assert.True(logFiles.Length == 0 || logFiles.All(f =>
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var seq = long.Parse(name[(name.IndexOf('-') + 1)..]);
            return seq > repo.Meta.LastSnapshottedSequence;
        }));

        // re-open should not change counts
        var repo2 = Repo.Open(_fs.Root);
        Assert.Equal(repo.Files.Count, repo2.Files.Count);
        Assert.Equal(repo.Dirs.Count, repo2.Dirs.Count);
    }

    [Fact]
    public void CompactNow_Forces_Snapshot_And_Prune()
    {
        var repo = Repo.Open(_fs.Root);
        var d = new DirRecord(Guid.NewGuid(), null, "root2");
        repo.CommitDelta(new RepoDelta(new(), new() { d }));

        // two files → two deltas
        for (int i = 0; i < 2; i++)
        {
            var f = new FileRecord(Guid.NewGuid(), d.Id, $"x{i}.bin", i, new byte[] { 0xAA }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 2);
            repo.CommitDelta(new RepoDelta(new() { f }, new()));
        }

        repo.CompactNow();

        // all deltas up to snapshot point pruned
        var logDir = Path.Combine(_fs.Root, "log");
        var remain = Directory.Exists(logDir) ? Directory.GetFiles(logDir, "*.delta").Length : 0;
        Assert.Equal(0, remain);

        // snapshot + indexes present and valid
        Assert.True(File.Exists(Path.Combine(_fs.Root, "snapshot.bin")));
        Assert.True(File.Exists(Path.Combine(_fs.Root, $"indexes-{repo.Meta.Generation}.bin")));
    }

    public void Dispose()
    {
        _fs.Dispose();
    }
}
