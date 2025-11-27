using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository;

public sealed class ScanSessionTests : IDisposable
{
    private readonly string _rootDir;

    public ScanSessionTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), "dff-scan-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDir);
    }

    private string SnapshotPath => Path.Combine(_rootDir, "snapshot.bin");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootDir))
                Directory.Delete(_rootDir, true);
        }
        catch
        {
            // ignore cleanup errors in tests
        }
    }

    private static IReadOnlyList<DirRecord> RealDirs(RepoSnapshot snapshot)
    {
        return snapshot.Dirs.Values.Where(d => d.Status != ScanEntryStatus.None).ToList();
    }

    private RepoSnapshot ReadSnapshot()
    {
        var bytes = File.ReadAllBytes(SnapshotPath);
        return MemoryPackSerializer.Deserialize<RepoSnapshot>(bytes)!;
    }

    // ReSharper disable once InconsistentNaming
    private static string RootPathForCurrentOS()
    {
        return OperatingSystem.IsWindows() ? "root" : "/root";
    }

    private static async Task<RepoSnapshot?> WaitForSnapshotAsync(
        Repo repo,
        string snapshotPath,
        Func<RepoSnapshot, bool> predicate,
        TimeSpan timeout)
    {
        var start = DateTime.UtcNow;

        while (true)
        {
            repo.SaveSnapshot();

            if (File.Exists(snapshotPath))
            {
                var bytes = await File.ReadAllBytesAsync(snapshotPath);
                var snapshot = MemoryPackSerializer.Deserialize<RepoSnapshot>(bytes);
                if (snapshot != null && predicate(snapshot))
                    return snapshot;
            }

            if (DateTime.UtcNow - start > timeout)
                return null;

            await Task.Delay(10);
        }
    }

    // --------------------------------------------------------------------
    //    Progressive flush: Observe* + FlushProgress should commit a delta
    //    and update repo state, but NOT mark the run completed.
    // --------------------------------------------------------------------
    [Fact]
    public async Task FlushProgress_CommitsObservedDirsAndFiles_WithoutCompletingRun()
    {
        var repo = Repo.Open(_rootDir);
        var rootPath = RootPathForCurrentOS();

        var session = repo.BeginScan(rootPath);

        session.AddOrUpdateDirectory(
            rootPath,
            ScanEntryStatus.Enumerated);

        var hashBytes = new byte[16];
        new Random(123).NextBytes(hashBytes);
        var hashKey = new HashKey(hashBytes);

        session.AddOrUpdateFile(
            Path.Combine(rootPath, "file.txt"),
            100,
            hashKey,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
             ScanEntryStatus.Hashed);

        await session.FlushProgressAsync(TestContext.Current.CancellationToken);

        // Do not call CompleteAsync / FailAsync here.

        repo.SaveSnapshot();
        var snapshot = ReadSnapshot();

        // Files and dirs should be present (ignoring dummy ancestors)
        var dirs = RealDirs(snapshot);
        Assert.Single(dirs);

        var dir = dirs[0];
        Assert.Equal("root", dir.Name);
        Assert.Equal(session.ScanSequence, dir.LastSeenSequence);

        var files = snapshot.Files.Values.ToList();
        Assert.Single(files);

        var file = files[0];
        Assert.Equal("file.txt", file.Name);
        Assert.Equal(session.ScanSequence, file.LastSeenScanSequence);


        // ScanRun should exist and still be InProgress
        var run = Assert.Single(snapshot.ScanRuns);
        Assert.Equal(session.ScanSequence, run.ScanSequence);
        Assert.Equal(ScanRunStatus.InProgress, run.Status);

        await session.DisposeAsync();
    }

    // --------------------------------------------------------------------
    //    CompleteAsync: progressive scan + completion should emit
    //    tombstones for entries under the root that were not seen
    //    in this scan sequence.
    // --------------------------------------------------------------------
    [Fact]
    public async Task CompleteAsync_EmitsTombstonesForUnseenEntriesUnderRoot()
    {
        var repo = Repo.Open(_rootDir);
        var rootPath = RootPathForCurrentOS();

        // Seed repo with an "old" file under /root/sub seen at scan sequence 1
        var rootDirId = Guid.NewGuid();
        var subDirId = Guid.NewGuid();
        var oldFileId = Guid.NewGuid();

        var hashBytes = new byte[16];
        new Random(111).NextBytes(hashBytes);
        var hash = new HashKey(hashBytes);
        var seq = repo.AllocateScanSequence();

        var rootDir = new DirRecord
        {
            Id = rootDirId,
            ParentId = null,
            Name = "root",
            LastSeenSequence = seq,
            Status = ScanEntryStatus.Enumerated
        };

        var subDir = new DirRecord
        {
            Id = subDirId,
            ParentId = rootDirId,
            Name = "sub",
            LastSeenSequence = seq,
            Status = ScanEntryStatus.Enumerated
        };

        var oldFile = new FileRecord
        {
            Id = oldFileId,
            DirId = subDirId,
            Name = "old.txt",
            Size = 1,
            Hash = hash,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = seq,
            Status = ScanEntryStatus.Enumerated
        };

        var scanSeq = repo.AllocateLogId();
        await repo.CommitDeltaAsync(new RepoDelta
        {
            ScanSequence = scanSeq,
            Dirs = [rootDir, subDir],
            Files = [oldFile]
        }, TestContext.Current.CancellationToken);

        repo.SaveSnapshot();
        var snapshot1 = ReadSnapshot();
        Assert.Single(snapshot1.Files);
        Assert.True(snapshot1.Files.ContainsKey(oldFileId));

        // New scan: only see a new file under the same root.
        var session = repo.BeginScan(rootPath);

        session.AddOrUpdateDirectory(
            "/root",
            ScanEntryStatus.Enumerated);

        session.AddOrUpdateDirectory(
            "/root/sub",
            ScanEntryStatus.Enumerated);

        session.AddOrUpdateFile(
            "/root/sub/new.txt",
            2,
            hash,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ScanEntryStatus.Hashed);

        // Progressive flush + completion
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);
        await session.CompleteAsync(TestContext.Current.CancellationToken);

        repo.SaveSnapshot();
        var snapshot2 = ReadSnapshot();
        
        // Only the new file under root/sub should remain.
        var files = snapshot2.Files.Values.ToList();
        Assert.Single(files);

        var remaining = files[0];
        Assert.Equal("new.txt", remaining.Name);
        Assert.Equal(subDirId, remaining.DirId); // still a meaningful check
        Assert.False(snapshot2.Files.ContainsKey(oldFileId));

        // ScanRun should be marked Completed for this sequence.
        var run = Assert.Single(snapshot2.ScanRuns, r => r.ScanSequence == session.ScanSequence);
        Assert.Equal(ScanRunStatus.Completed, run.Status);
    }

    // --------------------------------------------------------------------
    //    FailAsync: marking a scan as failed/cancelled should not
    //    generate tombstones, even if there was prior content.
    // --------------------------------------------------------------------
    [Fact]
    public async Task FailAsync_DoesNotEmitTombstones()
    {
        var repo = Repo.Open(_rootDir);
        var rootPath = RootPathForCurrentOS();

        // Seed with an existing file under root
        var dirId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        var hashBytes = new byte[16];
        new Random(222).NextBytes(hashBytes);
        var hash = new HashKey(hashBytes);

        var dir = new DirRecord
        {
            Id = dirId,
            ParentId = null,
            Name = "root",
            LastSeenSequence = 1,
            Status = ScanEntryStatus.Enumerated
        };

        var file = new FileRecord
        {
            Id = fileId,
            DirId = dirId,
            Name = "keep.txt",
            Size = 1,
            Hash = hash,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated
        };

        await repo.CommitDeltaAsync(new RepoDelta
        {
            ScanSequence = 1,
            Dirs = [dir],
            Files = [file]
        }, TestContext.Current.CancellationToken);

        repo.SaveSnapshot();
        var snapshot1 = ReadSnapshot();
        Assert.Single(snapshot1.Files);
        Assert.True(snapshot1.Files.ContainsKey(fileId));

        // Start a new scan but fail it.
        var session = repo.BeginScan(rootPath);

        // Optionally observe some stuff, but never complete.
        session.AddOrUpdateDirectory("root", ScanEntryStatus.Enumerated);
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);

        await session.FailAsync("cancelled", true, TestContext.Current.CancellationToken);

        repo.SaveSnapshot();
        var snapshot2 = ReadSnapshot();

        // Original file must still be present; no tombstone-based deletion.
        Assert.True(snapshot2.Files.ContainsKey(fileId));

        // ScanRun for this sequence should be Failed or Cancelled.
        var run = Assert.Single(snapshot2.ScanRuns, r => r.ScanSequence == session.ScanSequence);
        Assert.True(run.Status == ScanRunStatus.Failed || run.Status == ScanRunStatus.Cancelled);
        Assert.Equal("cancelled", run.ErrorMessage);
    }

    // --------------------------------------------------------------------
    //    DisposeAsync: if a ScanSession is disposed without CompleteAsync
    //    or FailAsync, it should mark the run as failed/cancelled and
    //    not emit deletions.
    // --------------------------------------------------------------------
    [Fact]
    public async Task DisposeAsync_WithoutCompletion_MarksRunFailedWithoutDeletions()
    {
        var repo = Repo.Open(_rootDir);
        var rootPath = RootPathForCurrentOS();

        // Seed with an existing file under root
        var dirId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        var hashBytes = new byte[16];
        new Random(333).NextBytes(hashBytes);
        var hash = new HashKey(hashBytes);

        var dir = new DirRecord
        {
            Id = dirId,
            ParentId = null,
            Name = "root",
            LastSeenSequence = 1,
            Status = ScanEntryStatus.Enumerated
        };

        var file = new FileRecord
        {
            Id = fileId,
            DirId = dirId,
            Name = "keep.txt",
            Size = 1,
            Hash = hash,
            Modified = DateTimeOffset.UtcNow,
            Created = DateTimeOffset.UtcNow,
            LastSeenScanSequence = 1,
            Status = ScanEntryStatus.Enumerated
        };

        await repo.CommitDeltaAsync(new RepoDelta
        {
            ScanSequence = 1,
            Dirs = [dir],
            Files = [file]
        }, TestContext.Current.CancellationToken);

        repo.SaveSnapshot();
        var snapshot1 = ReadSnapshot();
        Assert.True(snapshot1.Files.ContainsKey(fileId));

        // Start a new scan, observe something, but neither complete nor fail explicitly.
        var session = repo.BeginScan(rootPath);

        session.AddOrUpdateDirectory("root", ScanEntryStatus.Enumerated);
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);

        await session.DisposeAsync(); // should mark run failed/cancelled

        repo.SaveSnapshot();
        var snapshot2 = ReadSnapshot();

        // Original file must still be present.
        Assert.True(snapshot2.Files.ContainsKey(fileId));

        // ScanRun for this sequence should not be InProgress anymore.
        var run = Assert.Single(snapshot2.ScanRuns, r => r.ScanSequence == session.ScanSequence);
        Assert.NotEqual(ScanRunStatus.InProgress, run.Status);
    }

    // --------------------------------------------------------------------
    //    If thresholds are not reached, nothing is flushed automatically:
    //    - Snapshot before FlushProgress() should contain nothing.
    //    - Snapshot after FlushProgress() should contain the buffered entries.
    // --------------------------------------------------------------------
    [Fact]
    public async Task NoAutoFlush_BelowThreshold_RequiresExplicitFlush()
    {
        var repo = Repo.Open(_rootDir);
        var rootPath = RootPathForCurrentOS();

        // High thresholds: no auto-flush
        await using var session = repo.BeginScan(rootPath,  maxFilesBeforeFlush: 10, maxDirsBeforeFlush: 10);

        var hashBytes = new byte[16];
        new Random(456).NextBytes(hashBytes);
        var hashKey = new HashKey(hashBytes);

        session.AddOrUpdateDirectory(
            "root",
            ScanEntryStatus.Enumerated);

        session.AddOrUpdateFile(
            "root/f1.txt",
            10,
            hashKey,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ScanEntryStatus.Hashed);

        // Below threshold: nothing should have been flushed yet.
        repo.SaveSnapshot();
        var snapshotBefore = ReadSnapshot();

        // No real dirs/files flushed yet (dummy ancestors are OK)
        Assert.Empty(RealDirs(snapshotBefore));
        Assert.Empty(snapshotBefore.Files);


        // Now explicitly flush and snapshot again
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);
        repo.SaveSnapshot();
        var snapshotAfter = ReadSnapshot();

        var dirs = RealDirs(snapshotAfter);
        Assert.Single(dirs);
        Assert.Equal("root", dirs[0].Name);

        var files = snapshotAfter.Files.Values.ToList();
        Assert.Single(files);
        Assert.Equal("f1.txt", files[0].Name);
    }

    // --------------------------------------------------------------------
    //    Auto-flush when file threshold is reached (async).
    //    We rely on the background FlushProgressAsync() triggered by ObserveFile.
    // --------------------------------------------------------------------
    [Fact]
    public async Task AutoFlush_WhenFileThresholdReached_CommitsDelta_Async()
    {
        var repo = Repo.Open(_rootDir);
        var rootPath = RootPathForCurrentOS();

        var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 2);

        session.AddOrUpdateDirectory(
            "root",
            ScanEntryStatus.Enumerated);

        var hashBytes = new byte[16];
        new Random(123).NextBytes(hashBytes);
        var hashKey = new HashKey(hashBytes);

        // First file (buffered)
        session.AddOrUpdateFile(
            "root/f1.txt",
            10,
            hashKey,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
             ScanEntryStatus.Hashed);

        // Second file: exceeds threshold, should trigger auto FlushProgressAsync internally
        session.AddOrUpdateFile(
            "root/f2.txt",
            20,
            hashKey,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
             ScanEntryStatus.Hashed);

        // Wait until snapshot sees both files (or timeout)
        var snapshot = await WaitForSnapshotAsync(
            repo,
            SnapshotPath,
            s => s.Files.Count == 2 && RealDirs(s).Count == 1,
            TimeSpan.FromSeconds(2));

        Assert.NotNull(snapshot);

        var dirs = RealDirs(snapshot);
        Assert.Single(dirs);
        Assert.Equal("root", dirs[0].Name);

        Assert.Equal(2, snapshot.Files.Count);
        var names = snapshot.Files.Values.Select(f => f.Name).ToHashSet();
        Assert.True(names.SetEquals(new[] { "f1.txt", "f2.txt" }));


        await session.DisposeAsync();
    }

    // --------------------------------------------------------------------
    // 2. Auto-flush when directory threshold is reached (async).
    // --------------------------------------------------------------------
    [Fact]
    public async Task AutoFlush_WhenDirThresholdReached_CommitsDelta_Async()
    {
        var repo = Repo.Open(_rootDir);
        var rootPath = RootPathForCurrentOS();

        var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 1000, maxDirsBeforeFlush: 3);

        session.AddOrUpdateDirectory(
            "/root",
            ScanEntryStatus.Enumerated);

        // Second dir: exceeds threshold, should trigger auto flush
        session.AddOrUpdateDirectory(
            "/root/sub",
            ScanEntryStatus.Enumerated);

        var snapshot = await WaitForSnapshotAsync(
            repo,
            SnapshotPath,
            s => RealDirs(s).Count == 2,
            TimeSpan.FromSeconds(2));


        Assert.NotNull(snapshot);

        var dirs = RealDirs(snapshot);
        Assert.Equal(2, dirs.Count);

        var names = dirs.Select(d => d.Name).ToHashSet();
        Assert.True(names.SetEquals(new[] { "root", "sub" }));


        await session.DisposeAsync();
    }

    // --------------------------------------------------------------------
    //    Below threshold there is no auto-flush:
    //    - Snapshot should be empty until FlushProgressAsync is awaited.
    // --------------------------------------------------------------------
    [Fact]
    public async Task BelowThreshold_NoAutoFlush_RequiresExplicitFlushAsync()
    {
        var repo = Repo.Open(_rootDir);
        var rootPath = RootPathForCurrentOS();

        var session = repo.BeginScan(rootPath,  maxFilesBeforeFlush: 10, maxDirsBeforeFlush: 10);

        var hashBytes = new byte[16];
        new Random(456).NextBytes(hashBytes);
        var hashKey = new HashKey(hashBytes);

        session.AddOrUpdateDirectory(
            "root",
            ScanEntryStatus.Enumerated);

        session.AddOrUpdateFile(
            "root/f1.txt",
            10,
            hashKey,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ScanEntryStatus.Hashed);

        // Thresholds not reached -> no auto flush expected
        repo.SaveSnapshot();
        if (File.Exists(SnapshotPath))
        {
            var snapshotBefore = ReadSnapshot();
            Assert.Empty(snapshotBefore.Dirs);
            Assert.Empty(snapshotBefore.Files);
        }

        // Explicit async flush
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);

        repo.SaveSnapshot();
        var snapshotAfter = ReadSnapshot();

        var dirs = RealDirs(snapshotAfter);
        Assert.Single(dirs);
        Assert.Equal("root", dirs[0].Name);

        var files = snapshotAfter.Files.Values.ToList();
        Assert.Single(files);
        Assert.Equal("f1.txt", files[0].Name);


        await session.DisposeAsync();
    }

    // --------------------------------------------------------------------
    //    Multiple auto-flushes: threshold hit several times during the scan.
    //    After a final FlushProgressAsync(), all files must be persisted.
    // --------------------------------------------------------------------
    [Fact]
    public async Task AutoFlush_CanTriggerMultipleTimes_AllDataPersisted_Async()
    {
        var repo = Repo.Open(_rootDir);
        var rootPath = RootPathForCurrentOS();

        var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 2);

        session.AddOrUpdateDirectory(
            "/root",
            ScanEntryStatus.Enumerated);

        var hashBytes = new byte[16];
        new Random(789).NextBytes(hashBytes);
        var hashKey = new HashKey(hashBytes);

        var filenames = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var fn = $"f{i}.txt";
            filenames.Add(fn);

            session.AddOrUpdateFile(
                $"/root/{fn}",
                10 + i,
                hashKey,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ScanEntryStatus.Hashed);
            // Auto-flush should fire at i=1,3, and then we explicitly flush at the end.
        }

        // Final explicit drain of any remaining buffered files and in-flight auto flushes
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);

        repo.SaveSnapshot();
        var snapshot = ReadSnapshot();

        Assert.Single(snapshot.Dirs, d => d.Value.Status != ScanEntryStatus.None);
        Assert.Equal(filenames.Count, snapshot.Files.Count);

        foreach (var fn in filenames)
            Assert.True(snapshot.Files.Values.Any(f => f.Name == fn),
                $"Expected file '{fn}' to be present in snapshot.");

        await session.DisposeAsync();
    }
}