using System;
using System.IO;
using System.Linq;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLibTests.TestUtils;
using MemoryPack;
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
    public void SaveSnapshot_Writes_Indexes_File()
    {
        var repo = Repo.Open(_fs.Root);

        // one dir + one file then snapshot
        var dir = new DirRecord(Guid.NewGuid(), null, "root");
        var file = new FileRecord(Guid.NewGuid(), dir.Id, "a.bin", 1, Bytes(0xA5, 16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
        repo.CommitDelta(new RepoDelta(new() { file }, new() { dir }));

        repo.SaveSnapshot();

        var idxPath = IndexPath(_fs.Root, repo.Meta.Generation);
        Assert.True(File.Exists(idxPath));

        // sanity check on contents
        var idx = MemoryPackSerializer.Deserialize<RepoIndexes>(File.ReadAllBytes(idxPath));
        Assert.NotNull(idx);
        Assert.Equal(repo.Meta.Generation, idx.Generation);
        Assert.Single(idx.Buckets);
        var bucket = Assert.Single(idx.Buckets);
        Assert.Single(bucket.FileIds);
        Assert.Equal(file.Id, bucket.FileIds[0]);
    }

    [Fact]
    public void Open_Loads_Indexes_When_Present()
    {
        var repo = Repo.Open(_fs.Root);

        var d = new DirRecord(Guid.NewGuid(), null, "r");
        var f = new FileRecord(Guid.NewGuid(), d.Id, "b.bin", 2, Bytes(0x11, 16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 2);
        repo.CommitDelta(new RepoDelta(new() { f }, new() { d }));
        repo.SaveSnapshot(); // writes snapshot + indexes for current generation

        // reopen should load snapshot, then indexes, no deltas
        var repo2 = Repo.Open(_fs.Root);

        Assert.Single(repo2.Files);
        Assert.True(repo2.HashIndex.TryGetValue(f.Hash, out var ids));
        Assert.Single(ids);
        Assert.Equal(f.Id, ids[0]);
    }

    [Fact]
    public void Open_With_WrongGen_Index_Rebuilds_Index_File()
    {
        var repo = Repo.Open(_fs.Root);

        var d = new DirRecord(Guid.NewGuid(), null, "root2");
        var f = new FileRecord(Guid.NewGuid(), d.Id, "c.bin", 3, Bytes(0x22, 16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 3);
        repo.CommitDelta(new RepoDelta(new() { f }, new() { d }));
        repo.SaveSnapshot();

        var goodGen = repo.Meta.Generation;
        var idxPath = IndexPath(_fs.Root, goodGen);

        // Overwrite index with wrong generation header but same filename
        var wrong = new RepoIndexes(Generation: goodGen + 999, Buckets: new());
        File.WriteAllBytes(idxPath, MemoryPackSerializer.Serialize(wrong));

        // Reopen should detect gen mismatch and rebuild
        var repo2 = Repo.Open(_fs.Root);

        // HashIndex should still reflect the snapshot contents
        Assert.Single(repo2.Files);
        Assert.True(repo2.HashIndex.TryGetValue(f.Hash, out var ids));
        Assert.Single(ids);

        // Index file should be rewritten with correct generation
        var rewritten = MemoryPackSerializer.Deserialize<RepoIndexes>(File.ReadAllBytes(idxPath));
        Assert.NotNull(rewritten);
        Assert.Equal(goodGen, rewritten.Generation);
        Assert.Single(rewritten.Buckets);
    }

    [Fact]
    public void Open_With_Corrupted_Index_Rebuilds_Silently()
    {
        var repo = Repo.Open(_fs.Root);

        var d = new DirRecord(Guid.NewGuid(), null, "root3");
        var f = new FileRecord(Guid.NewGuid(), d.Id, "d.bin", 4, Bytes(0x33, 16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 4);
        repo.CommitDelta(new RepoDelta(new() { f }, new() { d }));
        repo.SaveSnapshot();

        var idxPath = IndexPath(_fs.Root, repo.Meta.Generation);
        File.WriteAllBytes(idxPath, new byte[] { 0x00, 0xFF, 0xEE, 0xDD }); // garbage

        var repo2 = Repo.Open(_fs.Root);

        // Still loads data and HashIndex rebuilt from snapshot
        Assert.Single(repo2.Files);
        Assert.True(repo2.HashIndex.TryGetValue(f.Hash, out var ids));
        Assert.Single(ids);

        // Index file should now be valid again
        var repaired = MemoryPackSerializer.Deserialize<RepoIndexes>(File.ReadAllBytes(idxPath));
        Assert.NotNull(repaired);
        Assert.Equal(repo2.Meta.Generation, repaired.Generation);
        Assert.Single(repaired.Buckets);
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
        Assert.True(repo2.HashIndex.TryGetValue(f1.Hash, out var ids));
        Assert.Equal(2, ids.Count);
        Assert.Contains(f1.Id, ids);
        Assert.Contains(f2.Id, ids);
    }

    [Fact]
    public void SaveSnapshot_After_Deltas_Rewrites_Indexes_For_Same_Generation()
    {
        var repo = Repo.Open(_fs.Root);

        var d = new DirRecord(Guid.NewGuid(), null, "root5");
        var f1 = new FileRecord(Guid.NewGuid(), d.Id, "g1.bin", 7, Bytes(0x55, 16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7);
        repo.CommitDelta(new RepoDelta(new() { f1 }, new() { d }));
        repo.SaveSnapshot();
        var gen = repo.Meta.Generation;
        var idxPath = IndexPath(_fs.Root, gen);

        // Add more files by deltas, then snapshot again
        var f2 = new FileRecord(Guid.NewGuid(), d.Id, "g2.bin", 8, Bytes(0x66, 16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 8);
        repo.CommitDelta(new RepoDelta(new() { f2 }, new()));

        repo.SaveSnapshot(); // rebuilds and overwrites index file for same generation

        var idx = MemoryPackSerializer.Deserialize<RepoIndexes>(File.ReadAllBytes(idxPath));
        Assert.NotNull(idx);
        // Should include buckets for both hashes now
        var hashes = idx.Buckets.Select(b => Convert.ToHexString(b.Hash)).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(Convert.ToHexString(f1.Hash), hashes);
        Assert.Contains(Convert.ToHexString(f2.Hash), hashes);
    }

    private static byte[] Bytes(byte val, int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = val;
        return b;
    }

    private static string IndexPath(string repoDir, long generation) =>
        Path.Combine(repoDir, $"indexes-{generation}.bin");

    public void Dispose()
    {
        try { if (Directory.Exists(_fs.Root)) Directory.Delete(_fs.Root, recursive: true); } catch { /* ignore */ }
    }
}
