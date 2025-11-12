using System;
using System.Collections.Generic;
using System.IO;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLibTests.TestUtils;
using MemoryPack;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository;

public sealed class RepoSnapshotCompatTests : IDisposable
{
    private readonly TempFsFixture _repoDir = new TempFsFixture("dff_repo_compat");

    [Fact]
    public void ReplayDeltas_After_V2_Snapshot_Extends_HashIndex()
    {
        var repo = Repo.Open(_repoDir.Root);

        var dir = new DirRecord(Guid.NewGuid(), null, "root");
        var h = Bytes(0x44, 16);
        var f1 = new FileRecord(Guid.NewGuid(), dir.Id, "e1.bin", 5, h, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 5);
        repo.CommitDelta(new RepoDelta(new() { f1 }, new() { dir }));

        // Write V2 snapshot (Repo.SaveSnapshot persists HashIndex inside snapshot)
        repo.SaveSnapshot();

        // Append new delta with same hash
        var f2 = new FileRecord(Guid.NewGuid(), dir.Id, "e2.bin", 6, h, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 6);
        repo.CommitDelta(new RepoDelta(new() { f2 }, new()));

        // Reopen: load V2 snapshot HashIndex, then replay delta → 2 ids for the same hash
        var repo2 = Repo.Open(_repoDir.Root);

        var key = HashKey.From(h);
        Assert.True(repo2.HashIndex.TryGetValue(key, out var ids));
        Assert.Equal(2, ids.Count);
        Assert.Contains(f1.Id, ids);
        Assert.Contains(f2.Id, ids);
    }

    [Fact]
    public void V1_Snapshot_Fallback_Rebuilds_HashIndex_Once()
    {
        // Craft a V1 snapshot (RepoSnapshot) manually
        var metaV1 = new RepoMeta { SchemaVersion = 1, Generation = 1, NextSequence = 0, LastSnapshottedSequence = 0 };
        var dir = new DirRecord(Guid.NewGuid(), null, "root");
        var h = Bytes(0xAA, 16);
        var f1 = new FileRecord(Guid.NewGuid(), dir.Id, "a.bin", 1, h, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
        var f2 = new FileRecord(Guid.NewGuid(), dir.Id, "b.bin", 2, h, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);

        var v1 = new RepoSnapshot(
            Meta: metaV1,
            Files: [f1, f2],
            Dirs: [dir],
            Strings: new Dictionary<string, Guid> ()
        );

        // Write snapshot.bin directly
        var snapPath = _repoDir.File("snapshot.bin", MemoryPackSerializer.Serialize(v1));

        // Ensure no deltas
        var logDir = _repoDir.Dir("log");
        foreach (var p in Directory.GetFiles(logDir, "*.delta")) File.Delete(p);

        // Open should rebuild HashIndex from V1
        var repo = Repo.Open(_repoDir.Root);
        var key = HashKey.From(h);
        Assert.True(repo.HashIndex.TryGetValue(key, out var ids));
        Assert.Equal(2, ids.Count);

        // Now save a V2 snapshot and verify it embeds the HashIndex
        repo.SaveSnapshot();
        var bytes = File.ReadAllBytes(snapPath);
        var v2 = MemoryPackSerializer.Deserialize<RepoSnapshotV2>(bytes);
        Assert.NotNull(v2);
        Assert.Equal(2, v2.HashIndex[key].Count);

        // Reopen again: should still see 2 without any rebuild needed
        var repo2 = Repo.Open(_repoDir.Root);
        Assert.True(repo2.HashIndex.TryGetValue(key, out var ids2));
        Assert.Equal(2, ids2.Count);
    }
    
    [Fact]
    public void SaveSnapshot_V2_Persists_HashIndex_InSnapsho()
    {
        var repo = Repo.Open(_repoDir.Root);
        var d = new DirRecord(Guid.NewGuid(), null, "root2");
        repo.CommitDelta(new RepoDelta(new(), new() { d }));

        // commit some files files
        var dummyHash = BitConverter.GetBytes(new UInt128(42, 42));
        var files = new List<FileRecord>();
        for (int i = 0; i < 4; i++)
        {
            var f = new FileRecord(Guid.NewGuid(), d.Id, $"x{i}.bin", i, dummyHash, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 2);
            files.Add(f);
        }
        repo.CommitDelta(new RepoDelta(files, [d]));
        repo.SaveSnapshot();
        
        var repo2 = Repo.Open(_repoDir.Root);
        Assert.True(repo2.HashIndex.Count == 1);
        Assert.True(repo2.HashIndex[HashKey.From(dummyHash)].Count == 4);
    }

    private static byte[] Bytes(byte val, int len)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = val;
        return b;
    }

    public void Dispose()
    {
        _repoDir.Dispose();
    }
}
