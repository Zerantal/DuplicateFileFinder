using System;
using System.IO;
using System.Linq;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository;

public sealed class ScanSessionTests : IDisposable
{
    private readonly TempFsFixture _scanRoot = new("dff_scan_root");
    private readonly TempFsFixture _repoRoot = new("dff_repo_root");

    [Fact]
    public void UpsertFile_Is_Idempotent_For_Same_Path()
    {
        var repo = Repo.Open(_repoRoot.Root);

        using (var s = repo.BeginScan(scanId: 7, rootPath: _scanRoot.Root))
        {
            var p = Path.Combine(_scanRoot.Root, "alpha", "file.bin");
            s.UpsertFile(p, size: 100, hash: HashBytes(0xAA, 16), modified: DateTimeOffset.UtcNow, created: DateTimeOffset.UtcNow);
            s.UpsertFile(p, size: 200, hash: HashBytes(0xAA, 16), modified: DateTimeOffset.UtcNow, created: DateTimeOffset.UtcNow);
            s.Commit();
        }

        // Only one record with updated size must remain
        Assert.Single(repo.Files);
        var fr = repo.Files.Values.Single();
        Assert.Equal("file.bin", fr.Name);
        Assert.Equal(200, fr.Size);

        // HashIndex should point to that single file id
        Assert.True(repo.HashIndex.TryGetValue(HashKey.From(fr.Hash), out var ids));
        Assert.Single(ids);
        Assert.Equal(fr.Id, ids[0]);
    }

    [Fact]
    public void UpsertFile_Creates_Ancestor_Directories()
    {
        var repo = Repo.Open(_repoRoot.Root);

        using (var s = repo.BeginScan(scanId: 8, rootPath: _scanRoot.Root))
        {
            var p = Path.Combine(_scanRoot.Root, "a", "b", "c", "deep.bin");
            s.UpsertFile(p, size: 1, hash: HashBytes(0x01, 16), modified: DateTimeOffset.UtcNow, created: DateTimeOffset.UtcNow);
            s.Commit();
        }

        // Expect at least 3 directories: a, b, c (root may or may not be present as its own node depending on platform root)
        Assert.True(repo.Dirs.Count >= 3);
        // Names exist
        var names = repo.Dirs.Values.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("a", names);
        Assert.Contains("b", names);
        Assert.Contains("c", names);

        var f = Assert.Single(repo.Files.Values);
        Assert.Equal("deep.bin", f.Name);
    }

    [Fact]
    public void Same_Hash_Different_Paths_Are_Grouped_As_Duplicates()
    {
        var repo = Repo.Open(_repoRoot.Root);

        using (var s = repo.BeginScan(scanId: 9, rootPath: _scanRoot.Root))
        {
            var p1 = Path.Combine(_scanRoot.Root, "d1", "dup.bin");
            var p2 = Path.Combine(_scanRoot.Root, "d2", "dup.bin");
            s.UpsertFile(p1, 10, HashBytes(0x55, 16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            s.UpsertFile(p2, 10, HashBytes(0x55, 16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            s.Commit();
        }

        Assert.Equal(2, repo.Files.Count);
        var any = repo.Files.Values.First();
        Assert.True(repo.HashIndex.TryGetValue(HashKey.From(any.Hash), out var ids));
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void FlushThreshold_Writes_MidScan_Delta()
    {
        var repo = Repo.Open(_repoRoot.Root);

        // thresholds set to 1 so the first UpsertFile triggers FlushDelta inside session
        using (var s = new ScanSession(repo, scanId: 10, rootPath: _scanRoot.Root, fileFlushThreshold: 1, dirFlushThreshold: 1))
        {
            var p = Path.Combine(_scanRoot.Root, "x", "y.bin");
            s.UpsertFile(p, 5, HashBytes(0x33, 16), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

            // After first upsert a delta file should already exist on disk
            var logDir = Path.Combine(_repoRoot.Root, "log");
            Assert.True(Directory.Exists(logDir));
            var deltasNow = Directory.GetFiles(logDir, "*.delta");
            Assert.True(deltasNow.Length >= 1);

            // Commit again to flush any remainder
            s.Commit();
        }

        // Data visible in repo
        Assert.Single(repo.Files);
        Assert.True(repo.HashIndex.Count >= 1);
    }

    private static ReadOnlySpan<byte> HashBytes(byte value, int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = value;
        return b;
    }

    public void Dispose()
    {
        _scanRoot.Dispose();
        _repoRoot.Dispose();
    }
}