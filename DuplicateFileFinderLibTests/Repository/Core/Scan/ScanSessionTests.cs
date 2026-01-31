// DuplicateFileFinderLibTests/Repository/Core/Scan/ScanSessionTests.cs

using System;
using System.Linq;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Storage.Models;

using DuplicateFileFinderLibTests.TestUtils;
using DuplicateFileFinderLibTests.TestUtils.Fakes;

using Xunit;

using ObservedDir = DuplicateFileFinderLib.Repository.Core.Models.ObservedDir;
using ObservedFile = DuplicateFileFinderLib.Repository.Core.Models.ObservedFile;
using ScanRun = DuplicateFileFinderLib.Repository.Storage.Models.ScanRun;

namespace DuplicateFileFinderLibTests.Repository.Core.Scan;

public sealed class ScanSessionTests
{
    [Fact]
    public void Ctor_WhenRootDirIdNotProvided_AllocatesAndExposesRootCursor()
    {
        var repo = new CapturingRepo
        {
            NextDirId = 100,
            BaselineView = null
        };

        var run = CreateRun(scanSequence: 7, scanRootId: 99);

        var session = new ScanSession(
            repo,
            run,
            rootDirInput: new DirScanInput
            {
                DirId = -1,
                ParentDirId = -1,
                Name = "ignored",
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.None,
                ErrorMessage = null
            });

        Assert.Equal(100, session.RootDirCursor.DirId);
        Assert.Equal(101, repo.NextDirId);
    }

    [Fact]
    public void BeginDirectory_PopulatesExpectedMaps_FromBaseline()
    {
        var baseline = new TestSnapshotViewBuilder()
            .Dir(dirId: 50, parentDirId: -1, name: "ROOT", status: ScanEntryStatus.Enumerated, lastSeenScanSequence: 1) // ignored by BaselineIndex
            .Dir(dirId: 101, parentDirId: 50, name: "D1", status: ScanEntryStatus.Enumerated, lastSeenScanSequence: 1)
            .File(fileId: 201, dirId: 50, name: "F1", status: ScanEntryStatus.Enumerated, lastSeenScanSequence: 1)
            .Build(scanRootId: 123);

        var repo = new CapturingRepo { BaselineView = baseline };
        var run = CreateRun(scanSequence: 1, scanRootId: 123);

        var session = NewSession(repo, run);

        var ctx = session.BeginDirectory(new DirCursor(50));

        Assert.Equal(50, ctx.ParentDirId);
        Assert.True(ctx.ExpectedDirs.ContainsKey("D1"));
        Assert.True(ctx.ExpectedFiles.ContainsKey("F1"));
    }

    [Fact]
    public async Task OnDirectoryFound_ReusesBaselineId_WhenNameMatchesExpected()
    {
        var baseline = new TestSnapshotViewBuilder()
            .Dir(dirId: 50, parentDirId: -1, name: "ROOT", status: ScanEntryStatus.Enumerated, lastSeenScanSequence: 1)
            .Dir(dirId: 101, parentDirId: 50, name: "D1", status: ScanEntryStatus.Enumerated, lastSeenScanSequence: 1)
            .Build(scanRootId: 123);

        var repo = new CapturingRepo { BaselineView = baseline, NextDirId = 1000 };
        var run = CreateRun(scanSequence: 5, scanRootId: 123);

        var session = NewSession(repo, run, 50);

        var ctx = session.BeginDirectory(new DirCursor(50));

        var child = session.OnDirectoryFound(
            new ObservedDir { Name = "D1", CreatedTicks = 10, ModifiedTicks = 11, ErrorMessage = null },
            ref ctx);

        Assert.Equal(101, child.DirId);              // reused, no allocation
        Assert.Equal(1000, repo.NextDirId);          // unchanged

        // Instead: force Complete to commit.

        await session.CompleteAsync(TestContext.Current.CancellationToken);
        var snap = repo.LastCommittedSnapshot!;
        Assert.True(snap.Value.Dirs.Length == 2);
        var d = snap.Value.Dirs.First(d => d.DirId == 101);

        Assert.Equal(101, d.DirId);
        Assert.Equal(50, d.ParentDirId);
        Assert.Equal(ScanEntryStatus.Enumerated, d.Status);
        Assert.Equal(5, d.LastSeenScanSequence);
        Assert.Equal("D1", snap.Value.StringPool.GetString(d.NameStrIdx));
    }

    [Fact]
    public async Task OnFileFound_ReusesBaselineId_WhenNameMatchesExpected()
    {
        var baseline = new TestSnapshotViewBuilder()
            .Dir(dirId: 50, parentDirId: -1, name: "ROOT", status: ScanEntryStatus.Enumerated, lastSeenScanSequence: 1)
            .File(fileId: 201, dirId: 50, name: "F1", status: ScanEntryStatus.Enumerated, lastSeenScanSequence: 1)
            .Build(scanRootId: 123);

        var repo = new CapturingRepo { BaselineView = baseline, NextFileId = 999 };
        var run = CreateRun(scanSequence: 2, scanRootId: 123);

        var session = NewSession(repo, run);
        var ctx = session.BeginDirectory(new DirCursor(50));

        _ = session.OnFileFound(
            new ObservedFile { Name = "F1", Size = 123, CreatedTicks = 1, ModifiedTicks = 2, ErrorMessage = null },
            ref ctx);

        await session.CompleteAsync(TestContext.Current.CancellationToken);

        var snap = repo.LastCommittedSnapshot!;
        var f = Assert.Single(snap.Value.Files);

        Assert.Equal(201, f.FileId);                 // reused baseline id
        Assert.Equal(50, f.DirId);
        Assert.Equal(123, f.Size);
        Assert.Equal(2, f.LastSeenScanSequence);
        Assert.Equal("F1", snap.Value.StringPool.GetString(f.NameStrIdx));
    }

    [Fact]
    public async Task EndDirectory_MarksUnseenExpectedEntries_AsDeleted()
    {
        var baseline = new TestSnapshotViewBuilder()
            .Dir(dirId: 50, parentDirId: -1, name: "ROOT", status: ScanEntryStatus.Enumerated, lastSeenScanSequence: 1)
            .Dir(dirId: 101, parentDirId: 50, name: "D1", status: ScanEntryStatus.Enumerated, lastSeenScanSequence: 1)
            .File(fileId: 201, dirId: 50, name: "F1", status: ScanEntryStatus.Enumerated, lastSeenScanSequence: 1)
            .Build(scanRootId: 123);

        var repo = new CapturingRepo { BaselineView = baseline };
        var run = CreateRun(scanSequence: 9, scanRootId: 123);

        var session = NewSession(repo, run);

        var ctx = session.BeginDirectory(new DirCursor(50));

        // Do NOT call OnDirectoryFound / OnFileFound => expected remain => deleted in EndDirectory
        session.EndDirectory(ref ctx);

        await session.CompleteAsync(TestContext.Current.CancellationToken);
        var snap = repo.LastCommittedSnapshot!;

        var delDir = Assert.Single(snap.Value.Dirs);
        Assert.Equal(101, delDir.DirId);
        Assert.Equal(ScanEntryStatus.Deleted, delDir.Status);

        var delFile = Assert.Single(snap.Value.Files);
        Assert.Equal(201, delFile.FileId);
        Assert.Equal(ScanEntryStatus.Deleted, delFile.Status);
    }

    [Fact]
    public async Task OnFileHashCompleted_WithHashBytes_AppliesHash()
    {
        var repo = new CapturingRepo { BaselineView = null };
        var run = CreateRun(scanSequence: 1, scanRootId: 1);
        var session = NewSession(repo, run);

        var ctx = session.BeginDirectory(new DirCursor(50));

        // Ensure record exists in mutation buffer
        _ = session.OnFileFound(
            new ObservedFile { Name = "X.bin", Size = 5, CreatedTicks = 0, ModifiedTicks = 0, ErrorMessage = null },
            ref ctx);

        var token = new FileHashToken(DirId: 50, Name: "X.bin", Size: 5);
        var bytes = new byte[16];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i + 1);

        session.OnFileHashCompleted(token, bytes, errorMessage: null);

        await session.CompleteAsync(TestContext.Current.CancellationToken);
        var snap = repo.LastCommittedSnapshot!;
        var f = Assert.Single(snap.Value.Files);

        Assert.Equal(new HashKey(bytes), f.Hash);
    }

    [Fact]
    public async Task OnFileHashCompleted_WithErrorMessage_SetsErrorStatus()
    {
        var repo = new CapturingRepo { BaselineView = null };
        var run = CreateRun(scanSequence: 1, scanRootId: 1);
        var session = NewSession(repo, run);

        var ctx = session.BeginDirectory(new DirCursor(50));

        _ = session.OnFileFound(
            new ObservedFile { Name = "X.bin", Size = 5, CreatedTicks = 0, ModifiedTicks = 0, ErrorMessage = null },
            ref ctx);

        var token = new FileHashToken(DirId: 50, Name: "X.bin", Size: 5);

        session.OnFileHashCompleted(token, ReadOnlyMemory<byte>.Empty, errorMessage: "hash failed");

        await session.CompleteAsync(TestContext.Current.CancellationToken);
        var snap = repo.LastCommittedSnapshot!;
        var f = Assert.Single(snap.Value.Files);

        Assert.Equal(ScanEntryStatus.Error, f.Status);
        Assert.NotEqual(-1, f.ErrorMessageStrIdx);
    }

    [Fact]
    public async Task CompleteAsync_CommitsSnapshot_AndMarksCompleted()
    {
        var repo = new CapturingRepo { BaselineView = null };
        var run = CreateRun(scanSequence: 123, scanRootId: 7);
        var session = NewSession(repo, run);

        await session.CompleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, repo.GetMethodCount("FinaliseCompletedScanAsync"));
        Assert.NotNull(repo.LastCommittedSnapshot);
        Assert.Equal(7, repo.LastCommittedSnapshot!.Value.ScanRootId);
    }

    [Fact]
    public async Task FailAsync_MarksFailed_AndDoesNotMarkCompleted()
    {
        var repo = new CapturingRepo { BaselineView = null };
        var run = CreateRun(scanSequence: 123, scanRootId: 7);
        var session = NewSession(repo, run);

        await session.FailAsync("boom", cancelled: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, repo.GetMethodCount("FinaliseCompletedScanAsync"));
        Assert.Equal(1, repo.GetMethodCount("MarkScanFailedAsync"));
        Assert.Equal("boom", repo.LastFailedMessage);
        Assert.False(repo.LastFailedCancelled);
    }

    [Fact]
    public async Task DisposeAsync_WhenNotFinished_MarksFailedCancelledTrue()
    {
        var repo = new CapturingRepo { BaselineView = null };
        var run = CreateRun(scanSequence: 1, scanRootId: 1);
        var session = NewSession(repo, run);

        await session.DisposeAsync();

        Assert.Equal(1, repo.GetMethodCount("MarkScanFailedAsync"));
        Assert.True(repo.LastFailedCancelled);
    }

    [Fact]
    public async Task CompleteAsync_EmptyRootDirectory_IncludesRootDirInSnapshot_WithStatusEnumerated_WhenRootIdProvided()
    {
        var repo = new CapturingRepo
        {
            NextDirId = 10_000, // should not be used in this test
            BaselineView = null
        };

        var run = CreateRun(scanSequence: 1, scanRootId: 123);

        // Root id is supplied -> session must use it
        var rootId = 777;

        var session = new ScanSession(
            repo,
            run,
            rootDirInput: new DirScanInput
            {
                DirId = rootId,
                ParentDirId = -1,
                Name = "",
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.None,
                ErrorMessage = null
            });

        // Enumerate the root directory once (empty)
        var ctx = session.BeginDirectory(session.RootDirCursor);
        session.EndDirectory(ref ctx);

        await session.CompleteAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(repo.LastCommittedSnapshot);
        var snap = repo.LastCommittedSnapshot.Value;

        // Root must be present; previously it was missing entirely.
        var root = Assert.Single(snap.Dirs);

        Assert.Equal(rootId, root.DirId);
        Assert.Equal(-1, root.ParentDirId);
        Assert.Equal(ScanEntryStatus.Enumerated, root.Status);
        Assert.Equal(run.ScanSequence, root.LastSeenScanSequence);

        // Sanity: name is "" (root)
        Assert.Equal(string.Empty, snap.StringPool.GetString(root.NameStrIdx));
    }

    [Fact]
    public async Task CompleteAsync_EmptyRootDirectory_IncludesRootDirInSnapshot_WithStatusEnumerated_WhenRootIdAllocated()
    {
        var repo = new CapturingRepo
        {
            NextDirId = 1000, // will be used to allocate root
            BaselineView = null
        };

        var run = CreateRun(scanSequence: 5, scanRootId: 1);

        var session = new ScanSession(
            repo,
            run,
            rootDirInput: new DirScanInput
            {
                DirId = -1, // force allocation
                ParentDirId = -1,
                Name = "",
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.None,
                ErrorMessage = null
            });

        // allocation should have happened in ctor
        Assert.Equal(1000, session.RootDirCursor.DirId);

        // Enumerate the root directory once (empty)
        var ctx = session.BeginDirectory(session.RootDirCursor);
        session.EndDirectory(ref ctx);

        await session.CompleteAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(repo.LastCommittedSnapshot);
        var snap = repo.LastCommittedSnapshot.Value;

        var root = Assert.Single(snap.Dirs);
        Assert.Equal(1000, root.DirId);
        Assert.Equal(ScanEntryStatus.Enumerated, root.Status);
    }

    [Fact]
    public async Task FlushProgressAsync_TimeBased_NoFasterThanInterval_WritesAtMostOneCheckpoint()
    {
        // Fake clock
        long now = new DateTime(2025, 01, 01, 0, 0, 0, DateTimeKind.Utc).Ticks;
        // ReSharper disable once AccessToModifiedClosure
        long Clock() => now;

        var repo = new CapturingRepo();
        var run = new ScanRun
        {
            ScanSequence = 10,
            ScanRootId = 1,
            RootPath = "/tmp",
            StartedAt = DateTimeOffset.UtcNow,
            Status = ScanRunStatus.InProgress
        };

        // Create session with short interval for test
        var session = new ScanSession(
            repo,
            run,
            rootDirInput: new DirScanInput
            {
                DirId = 50,
                ParentDirId = -1,
                Name = "",
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.None,
                ErrorMessage = null
            },
            minCheckpointInterval: TimeSpan.FromSeconds(30),
            utcNowTicks: Clock);

        // Ensure root is enumerated so it can appear in drained delta if your Drain captures it
        var ctx = session.BeginDirectory(session.RootDirCursor);
        session.EndDirectory(ref ctx);

        // Mutate: add a file (marks "dirty" inside ScanSession via Volatile.Write)
        var ctx2 = session.BeginDirectory(session.RootDirCursor);
        session.OnFileFound(
            new ObservedFile
            {
                Name = "a.bin",
                Size = 10,
                CreatedTicks = 1,
                ModifiedTicks = 2,
                ErrorMessage = null
            },
            ref ctx2);
        session.EndDirectory(ref ctx2);

        // Immediate second flush should not write (interval not elapsed, and likely no new drained deltas)
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, repo.GetMethodCount("CommitCheckpoint"));

        // Mutate again within interval
        var ctx3 = session.BeginDirectory(session.RootDirCursor);
        session.OnFileFound(
            new ObservedFile
            {
                Name = "b.bin",
                Size = 10,
                CreatedTicks = 3,
                ModifiedTicks = 4,
                ErrorMessage = null
            },
            ref ctx3);
        session.EndDirectory(ref ctx3);

        // Still within 30s => no checkpoint
        now += TimeSpan.FromSeconds(10).Ticks;
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, repo.GetMethodCount("CommitCheckpoint"));

        // Advance beyond interval => checkpoint written
        now += TimeSpan.FromSeconds(25).Ticks; // total 35s since last
        await session.FlushProgressAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, repo.GetMethodCount("CommitCheckpoint"));
    }

    [Fact]
    public async Task ImportPartialSnapshot_WhenCompleted_CommitsImportedDirsAndFiles_WithNamesFromStringPool()
    {
        // Build a partial snapshot that contains a small tree + one file.
        // IMPORTANT: scanRootId matches the run so CommitSnapshotV2 is consistent.
        const ScanRootId scanRootId = 123;
        const long scanSequence = 77;

        // Pool indices:
        // 0 => "" (root)
        // 1 => "D1"
        // 2 => "F1.bin"
        var pool = PackedStringPool.FromStrings(["", "D1", "F1.bin"]);

        var partial = new ScanRootSnapshotV2
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs =
            [
                new DirRecordV2
                {
                    DirId = 50,
                    ParentDirId = -1,
                    NameStrIdx = 0,
                    Status = ScanEntryStatus.Enumerated,
                    LastSeenScanSequence = scanSequence,
                    ErrorMessageStrIdx = -1,
                    CreatedTicks = 0,
                    ModifiedTicks = 0
                },
                new DirRecordV2
                {
                    DirId = 101,
                    ParentDirId = 50,
                    NameStrIdx = 1,
                    Status = ScanEntryStatus.Enumerated,
                    LastSeenScanSequence = scanSequence,
                    ErrorMessageStrIdx = -1,
                    CreatedTicks = 10,
                    ModifiedTicks = 11
                }
            ],
            Files =
            [
                new FileRecordV2
                {
                    FileId = 201,
                    DirId = 101,
                    NameStrIdx = 2,
                    Size = 123,
                    Hash = HashKey.NotComputed,
                    Status = ScanEntryStatus.Enumerated,
                    LastSeenScanSequence = scanSequence,
                    ErrorMessageStrIdx = -1,
                    CreatedTicks = 1,
                    ModifiedTicks = 2
                }
            ]
        };

        var repo = new CapturingRepo { BaselineView = null };
        var run = CreateRun(scanSequence, scanRootId);

        // Root DirId must match (50) so we don't end up with competing roots.
        var session = new ScanSession(
            repo,
            run,
            rootDirInput: new DirScanInput
            {
                DirId = 50,
                ParentDirId = -1,
                Name = "",
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.None,
                ErrorMessage = null
            });

        session.ImportPartialSnapshot(partial);

        await session.CompleteAsync(TestContext.Current.CancellationToken);

        var committed = repo.LastCommittedSnapshot!.Value;

        var d1 = committed.Dirs.Single(d => d.DirId == 101);
        Assert.Equal(50, d1.ParentDirId);
        Assert.Equal("D1", committed.StringPool.GetString(d1.NameStrIdx));

        var f1 = committed.Files.Single(f => f.FileId == 201);
        Assert.Equal(101, f1.DirId);
        Assert.Equal("F1.bin", committed.StringPool.GetString(f1.NameStrIdx));
    }

    [Fact]
    public async Task ImportPartialSnapshot_ThenNewFinds_CommitsUnionOfImportedAndNew()
    {
        const ScanRootId scanRootId = 123;
        const long scanSequence = 88;

        var pool = PackedStringPool.FromStrings(["", "D1", "F1.bin"]);

        var partial = new ScanRootSnapshotV2
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs =
            [
                new DirRecordV2
                {
                    DirId = 50,
                    ParentDirId = -1,
                    NameStrIdx = 0,
                    Status = ScanEntryStatus.Enumerated,
                    LastSeenScanSequence = scanSequence,
                    ErrorMessageStrIdx = -1
                },
                new DirRecordV2
                {
                    DirId = 101,
                    ParentDirId = 50,
                    NameStrIdx = 1,
                    Status = ScanEntryStatus.Enumerated,
                    LastSeenScanSequence = scanSequence,
                    ErrorMessageStrIdx = -1
                }
            ],
            Files =
            [
                new FileRecordV2
                {
                    FileId = 201,
                    DirId = 101,
                    NameStrIdx = 2,
                    Size = 123,
                    Hash = HashKey.NotComputed,
                    Status = ScanEntryStatus.Enumerated,
                    LastSeenScanSequence = scanSequence,
                    ErrorMessageStrIdx = -1
                }
            ]
        };

        var repo = new CapturingRepo
        {
            BaselineView = null,
            NextFileId = 10_000 // so new file allocation is obvious
        };

        var run = CreateRun(scanSequence, scanRootId);

        var session = new ScanSession(
            repo,
            run,
            rootDirInput: new DirScanInput
            {
                DirId = 50,
                ParentDirId = -1,
                Name = "",
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.None,
                ErrorMessage = null
            });

        session.ImportPartialSnapshot(partial);

        // Add a NEW file in the imported directory (101).
        var ctx = session.BeginDirectory(new DirCursor(101));
        _ = session.OnFileFound(
            new ObservedFile
            {
                Name = "NEW.bin",
                Size = 999,
                CreatedTicks = 1,
                ModifiedTicks = 2,
                ErrorMessage = null
            },
            ref ctx);
        session.EndDirectory(ref ctx);

        await session.CompleteAsync(TestContext.Current.CancellationToken);

        var committed = repo.LastCommittedSnapshot!.Value;

        Assert.Contains(committed.Files, f => committed.StringPool.GetString(f.NameStrIdx) == "F1.bin");
        Assert.Contains(committed.Files, f => committed.StringPool.GetString(f.NameStrIdx) == "NEW.bin");

        var newFile = committed.Files.Single(f => committed.StringPool.GetString(f.NameStrIdx) == "NEW.bin");
        Assert.True(newFile.FileId >= 10_000);
        Assert.Equal(101, newFile.DirId);
    }

    // ---------------- helpers ----------------

    private static ScanSession NewSession(CapturingRepo repo, ScanRun run, DirId rootDirId = -1)
        => new(
            repo,
            run,
            rootDirInput: new DirScanInput
            {
                DirId = rootDirId,
                ParentDirId = -1,
                Name = "",
                CreatedTicks = 0,
                ModifiedTicks = 0,
                Status = ScanEntryStatus.None,
                ErrorMessage = null
            });

    private static ScanRun CreateRun(long scanSequence, ScanRootId scanRootId)
        => new()
        {
            ScanSequence = scanSequence,
            ScanRootId = scanRootId,
            RootPath = "/tmp",
            StartedAt = DateTimeOffset.UtcNow,
            Status = ScanRunStatus.InProgress
        };


}
