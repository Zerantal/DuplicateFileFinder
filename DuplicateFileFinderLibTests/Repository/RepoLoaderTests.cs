using System;
using System.IO;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository;

public sealed class RepoLoaderTests : IDisposable
{
    private readonly TempFsFixture _fs;

    public RepoLoaderTests()
    {
        _fs = new TempFsFixture("dff_repo_loader_tests");
    }

    [Fact]
    public void ReplayDeltas_Applies_After_Index_Load()
    {
        // Build snapshot with one file
        var repo = Repo.Open(_fs.Root);
        var d = new DirRecord(Guid.NewGuid(), null, "root4");
        var f1 = new FileRecord(Guid.NewGuid(), d.Id, "e1.bin", 5, Bytes(0x44, 16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 5);
        repo.CommitDelta(new RepoDelta(new() { f1 }, new() { d }));
        repo.SaveSnapshot();

        // Now append a delta with another file
        var f2 = new FileRecord(Guid.NewGuid(), d.Id, "e2.bin", 6, Bytes(0x44, 16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 6);
        repo.CommitDelta(new RepoDelta(new() { f2 }, new()));

        // Reopen: should load snapshot + indexes, then replay delta to include f2
        var repo2 = Repo.Open(_fs.Root);

        Assert.Equal(2, repo2.Files.Count);

        // Both files share the same hash; HashIndex should have two ids after replay
        Assert.True(repo2.HashIndex.TryGetValue(HashKey.From(f1.Hash), out var ids));
        Assert.Equal(2, ids.Count);
        Assert.Contains(f1.Id, ids);
        Assert.Contains(f2.Id, ids);
    }

    private static byte[] Bytes(byte val, int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = val;
        return b;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_fs.Root)) Directory.Delete(_fs.Root, recursive: true); } catch { /* ignore */ }
    }
}
