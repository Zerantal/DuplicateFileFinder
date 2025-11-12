using System;
using System.IO;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository;

public sealed class RepoTests : IDisposable
{
    private readonly TempFsFixture _fs;

    public RepoTests()
    {
        _fs = new TempFsFixture("dff_repo_loader_tests");
    }

    [Fact]
    public void Open_EmptyRepo_CreatesMeta_And_IsEmpty()
    {
        var repo = Repo.Open(_fs.Root);

        Assert.NotNull(repo.Meta);
        Assert.True(File.Exists(Path.Combine(_fs.Root, "meta.json")));
        Assert.Empty(repo.Files);
        Assert.Empty(repo.Dirs);
    }

    [Fact]
    public void CommitDelta_Persists_And_Replays_On_Reopen()
    {
        var repo = Repo.Open(_fs.Root);

        var dir = new DirRecord(Guid.NewGuid(), null, "root");
        var file = new FileRecord(
            Id: Guid.NewGuid(),
            DirId: dir.Id,
            Name: "a.bin",
            Size: 123,
            Hash: RandomHash(16),
            Modified: DateTimeOffset.UtcNow,
            Created: DateTimeOffset.UtcNow,
            ScanId: 1);

        repo.CommitDelta(new RepoDelta(
            Files: new() { file },
            Dirs: new() { dir }));

        // Fresh reopen. No snapshot yet, so it must replay deltas.
        var repo2 = Repo.Open(_fs.Root);

        Assert.Single(repo2.Dirs);
        Assert.Single(repo2.Files);
        var f = Assert.Single(repo2.Files.Values);
        Assert.Equal("a.bin", f.Name);
        Assert.True(repo2.HashIndex.TryGetValue(HashKey.From(f.Hash), out var ids));
        Assert.Single(ids);
        Assert.Equal(f.Id, ids[0]);
    }

    [Fact]
    public void SaveSnapshot_Allows_Reopen_Without_Log()
    {
        var repo = Repo.Open(_fs.Root);
        var dir = new DirRecord(Guid.NewGuid(), null, "root");
        var f1 = new FileRecord(Guid.NewGuid(), dir.Id, "x.bin", 1, RandomHash(16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
        repo.CommitDelta(new RepoDelta(new() { f1 }, new() { dir }));

        repo.SaveSnapshot();

        // simulate log cleanup by deleting *.delta
        var logDir = Path.Combine(_fs.Root, "log");
        if (Directory.Exists(logDir))
        {
            foreach (var p in Directory.GetFiles(logDir, "*.delta"))
                File.Delete(p);
        }

        var repo2 = Repo.Open(_fs.Root);
        var fx = Assert.Single(repo2.Files.Values);
        Assert.Equal("x.bin", fx.Name);
        Assert.True(File.Exists(Path.Combine(_fs.Root, "snapshot.bin")));
    }

    private static byte[] RandomHash(int len)
    {
        var b = new byte[len];
        new Random(42).NextBytes(b);
        return b;
    }

    public void Dispose()
    {
        _fs.Dispose();
    }
}
