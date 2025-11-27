using System;
using System.Collections.Generic;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class RepoSnapshotTests
{
    [Fact]
    public void RepoSnapshot_MemoryPackRoundTrip_PreservesAllSubStructures()
    {
        var repoId = Guid.NewGuid();

        var meta = new RepoMeta
        {
            SchemaVersion = 4,
            Generation = 1,
            NextLogSequence = 10,
            LastSnapshottedLogSequence = 8,
            LastCompaction = new DateTimeOffset(2024, 4, 5, 6, 7, 8, TimeSpan.Zero),
            RepoId = repoId,
            RepoPath = "/repo",
            RepoHostName = "host",
            NextScanSequence = 20
        };

        var dirId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var hashBytes = new byte[16];
        new Random(321).NextBytes(hashBytes);
        var hashKey = new HashKey(hashBytes);

        var dir = new DirRecord
        {
            Id = dirId,
            ParentId = null,
            Name = "root",
            LastSeenSequence = 1,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        };

        var file = new FileRecord
        {
            Id = fileId,
            DirId = dirId,
            Name = "f",
            Size = 123,
            Hash = hashKey,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        };

        var scanRun = new ScanRun
        {
            ScanSequence = 1,
            RootPath = "/root",
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = null,
            Status = ScanRunStatus.InProgress,
            ErrorMessage = null,
            ScanRootId = Guid.NewGuid(),
            Mode = ScanMode.Full
        };
        var scanRoot = new ScanRoot
        {
            Id = Guid.NewGuid(),
            RootPath = "/root",
            DirId = dirId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var files = new Dictionary<Guid, FileRecord> { [fileId] = file };
        var dirs = new Dictionary<Guid, DirRecord> { [dirId] = dir };
        var hashIndex = new Dictionary<HashKey, List<Guid>> { [hashKey] = [fileId] };
        var scanRuns = new List<ScanRun> { scanRun };
        var scanRoots = new List<ScanRoot> { scanRoot };

        var snapshot = new RepoSnapshot
        {
            Meta = meta,
            Files = files,
            Dirs = dirs,
            HashIndex = hashIndex,
            ScanRuns = scanRuns,
            ScanRoots = scanRoots
        };

        var bytes = MemoryPackSerializer.Serialize(snapshot);
        var roundTripped = MemoryPackSerializer.Deserialize<RepoSnapshot>(bytes)!;

        Assert.Equal(snapshot.Meta, roundTripped.Meta);
        Assert.Equal(snapshot.Files.Count, roundTripped.Files.Count);
        Assert.Equal(snapshot.Dirs.Count, roundTripped.Dirs.Count);
        Assert.Equal(snapshot.HashIndex.Count, roundTripped.HashIndex.Count);
        Assert.Equal(snapshot.ScanRuns.Count, roundTripped.ScanRuns.Count);

        var rtFile = Assert.Single(roundTripped.Files);
        Assert.Equal(fileId, rtFile.Key);

        var rtDir = Assert.Single(roundTripped.Dirs);
        Assert.Equal(dirId, rtDir.Key);

        var rtHashEntry = Assert.Single(roundTripped.HashIndex);
        Assert.Equal(hashKey, rtHashEntry.Key);
        Assert.Single(rtHashEntry.Value, id => id == fileId);

        var rtScanRun = Assert.Single(roundTripped.ScanRuns);
        Assert.Equal(scanRun.ScanSequence, rtScanRun.ScanSequence);
        Assert.Equal(scanRun.RootPath, rtScanRun.RootPath);
        
        var rtScanRoot = Assert.Single(roundTripped.ScanRoots);
        Assert.Equal(scanRoot, rtScanRoot);
        Assert.Equal(scanRoot.DirId, rtScanRoot.DirId);
    }
}