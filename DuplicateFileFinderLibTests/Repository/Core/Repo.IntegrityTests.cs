using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Core;

public sealed class RepoIntegrityTests : IDisposable
{
    private readonly string _repoDir;

    public RepoIntegrityTests()
    {
        _repoDir = Path.Combine(
            Path.GetTempPath(),
            "dff-repo-integrity-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_repoDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_repoDir))
                Directory.Delete(_repoDir, true);
        }
        catch
        {
            // ignore clean-up errors
        }
    }

    // --------------------------------------------------------------------
    // Helpers to get at private state under _sync
    // --------------------------------------------------------------------

    private static object GetSyncObject(Repo repo)
    {
        var f = typeof(Repo).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        return f.GetValue(repo)!;
    }

    private static Dictionary<long, ScanRoot> GetScanRoots(Repo repo)
    {
        var f = typeof(Repo).GetField("_scanRoots", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        return (Dictionary<long, ScanRoot>)f.GetValue(repo)!;
    }

    private static Dictionary<long, ScanRootSnapshotV2> GetScanRootSnapshots(Repo repo)
    {
        var f = typeof(Repo).GetField("_scanRootSnapshots", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        return (Dictionary<long, ScanRootSnapshotV2>)f.GetValue(repo)!;
    }

    private static PackedStringPool Pool(params string[] s) => PackedStringPool.FromStrings(s);

    private static ScanRootSnapshotV2 MakeSnapshot(
        long scanRootId,
        PackedStringPool pool,
        DirRecordV2[] dirs,
        FileRecordV2[] files)
        => new()
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs = dirs,
            Files = files
        };

    private static ScanRoot MakeRoot(long rootId, string rootPath, long dirId, bool deleted = false)
        => new()
        {
            RootId = rootId,
            RootPath = rootPath,
            DirId = dirId,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastScannedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            IsDeleted = deleted
        };

    // ====================================================================
    // 1) repo.mp sanity
    // ====================================================================

    [Fact]
    public async Task ValidateIntegrity_ReportsMetaMissing_WhenRepoMpDeleted()
    {
        var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

        // repo.mp should exist after open; delete it
        var metaPath = Path.Combine(_repoDir, "repo.mp");
        Assert.True(File.Exists(metaPath));
        File.Delete(metaPath);
        Assert.False(File.Exists(metaPath));

        var issues = repo.ValidateIntegrity(deepConsistencyCheck: false, ct: TestContext.Current.CancellationToken);

        Assert.Contains(issues, i => i.Code == "META_MISSING");

        await repo.DisposeAsync();
    }

    // ====================================================================
    // 2) Root dir must exist in that root's snapshot (V2)
    // ====================================================================

    [Fact]
    public async Task ValidateIntegrity_Errors_WhenRootDirIdNotPresentInSnapshot()
    {
        var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

        var sync = GetSyncObject(repo);
        var roots = GetScanRoots(repo);
        var snaps = GetScanRootSnapshots(repo);

        lock (sync)
        {
            roots.Clear();
            snaps.Clear();

            // Root claims dirId=100 but snapshot contains only dirId=10
            roots[1] = MakeRoot(rootId: 1, rootPath: "/r", dirId: 100);

            var pool = Pool("root");
            snaps[1] = MakeSnapshot(
                scanRootId: 1,
                pool: pool,
                dirs:
                [
                    new DirRecordV2
            {
                        DirId = 10,
                        ParentDirId = 0,
                        NameStrIdx = 0,
                        Status = ScanEntryStatus.Enumerated,
                        LastSeenScanSequence = 1
                    }
                ],
                files: []);
        }

        var issues = repo.ValidateIntegrity(ct: TestContext.Current.CancellationToken);

        // Adjust code string to match your refactor if needed.
        Assert.Contains(issues, i => i.Code is "ROOT_DIRID_MISSING_IN_SNAPSHOT" or "ROOT_DIRID_MISSING");

        await repo.DisposeAsync();
    }

    // ====================================================================
    // 3) Dir parent validation
    // ====================================================================

    [Fact]
    public async Task ValidateIntegrity_Errors_WhenDirParentMissing()
    {
        var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

        var sync = GetSyncObject(repo);
        var roots = GetScanRoots(repo);
        var snaps = GetScanRootSnapshots(repo);

        lock (sync)
        {
            roots.Clear();
            snaps.Clear();

            roots[1] = MakeRoot(rootId: 1, rootPath: "/r", dirId: 10);

            var pool = Pool("root", "child");
            snaps[1] = MakeSnapshot(
                scanRootId: 1,
                pool: pool,
                dirs:
                [
                    new DirRecordV2
            {
                        DirId = 10,
                        ParentDirId = 0,
                        NameStrIdx = 0,
                        Status = ScanEntryStatus.Enumerated
                    },
                    new DirRecordV2
            {
                        DirId = 11,
                        ParentDirId = 999, // missing
                        NameStrIdx = 1,
                        Status = ScanEntryStatus.Enumerated
                    }
                ],
                files: []);
        }

        var issues = repo.ValidateIntegrity(ct: TestContext.Current.CancellationToken);

        Assert.Contains(issues, i => i.Code == "DIR_PARENT_MISSING");

        await repo.DisposeAsync();
    }

    // ====================================================================
    // 4) Dir cycle detection
    // ====================================================================

    [Fact]
    public async Task ValidateIntegrity_Errors_WhenDirCycleExists()
    {
        var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

        var sync = GetSyncObject(repo);
        var roots = GetScanRoots(repo);
        var snaps = GetScanRootSnapshots(repo);

        lock (sync)
        {
            roots.Clear();
            snaps.Clear();

            roots[1] = MakeRoot(rootId: 1, rootPath: "/r", dirId: 10);

            var pool = Pool("a", "b");
            snaps[1] = MakeSnapshot(
                scanRootId: 1,
                pool: pool,
                dirs:
                [
                    // 10 -> 11
                    new DirRecordV2
            {
                        DirId = 10,
                        ParentDirId = 11,
                        NameStrIdx = 0,
                        Status = ScanEntryStatus.Enumerated
                    },
                    // 11 -> 10 (cycle)
                    new DirRecordV2
            {
                        DirId = 11,
                        ParentDirId = 10,
                        NameStrIdx = 1,
                        Status = ScanEntryStatus.Enumerated
                    }
                ],
                files: []);
        }

        var issues = repo.ValidateIntegrity(ct: TestContext.Current.CancellationToken);

        // Adjust if your refactor uses a different code.
        Assert.Contains(issues, i => i.Code is "DIR_CYCLE" or "DIR_CYCLE_DETECTED");

        await repo.DisposeAsync();
    }

    // ====================================================================
    // 5) File -> dir references
    // ====================================================================

    [Fact]
    public async Task ValidateIntegrity_Errors_WhenFileDirMissing()
    {
        var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

        var sync = GetSyncObject(repo);
        var roots = GetScanRoots(repo);
        var snaps = GetScanRootSnapshots(repo);

        lock (sync)
        {
            roots.Clear();
            snaps.Clear();

            roots[1] = MakeRoot(rootId: 1, rootPath: "/r", dirId: 10);

            var pool = Pool("root", "file.txt");
            snaps[1] = MakeSnapshot(
                scanRootId: 1,
                pool: pool,
                dirs:
                [
                    new DirRecordV2
                    {
                        DirId = 10,
                        ParentDirId = 0,
                        NameStrIdx = 0,
                        Status = ScanEntryStatus.Enumerated
                    }
                ],
                files:
                [
                    new FileRecordV2
                    {
                        FileId = 500,
                        DirId = 999, // missing
                        NameStrIdx = 1,
                        Size = 123,
                        Status = ScanEntryStatus.Enumerated
        }
                ]);
        }

        var issues = repo.ValidateIntegrity(ct: TestContext.Current.CancellationToken);

        Assert.Contains(issues, i => i.Code is "FILE_DIR_MISSING" or "ROOT_SNAPSHOT_FILE_DIR_MISSING");

        await repo.DisposeAsync();
    }

    // ====================================================================
    // 6) String pool bounds checking (NameStrIdx / ErrorMessageStrIdx)
    // ====================================================================

    [Fact]
    public async Task ValidateIntegrity_Errors_WhenNameStrIdxOutOfRange()
    {
        var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

        var sync = GetSyncObject(repo);
        var roots = GetScanRoots(repo);
        var snaps = GetScanRootSnapshots(repo);

        lock (sync)
        {
            roots.Clear();
            snaps.Clear();

            roots[1] = MakeRoot(rootId: 1, rootPath: "/r", dirId: 10);

            var pool = Pool("root"); // Count = 1, valid idx is 0 only
            snaps[1] = MakeSnapshot(
                scanRootId: 1,
                pool: pool,
                dirs:
                [
                    new DirRecordV2
            {
                        DirId = 10,
                        ParentDirId = 0,
                        NameStrIdx = 7, // OOB
                Status = ScanEntryStatus.Enumerated
                    }
                ],
                files:
                [
                    new FileRecordV2
                    {
                        FileId = 1,
                        DirId = 10,
                        NameStrIdx = 99, // OOB
                        Size = 1,
                Status = ScanEntryStatus.Enumerated
                    }
                ]);
        }

        var issues = repo.ValidateIntegrity(ct: TestContext.Current.CancellationToken);

        // These codes depend on how you implemented the OOB checks.
        Assert.Contains(issues, i => i.Code is "DIR_NAMEIDX_OOB" or "ROOT_SNAPSHOT_DIR_NAMEIDX_OOB");
        Assert.Contains(issues, i => i.Code is "FILE_NAMEIDX_OOB" or "ROOT_SNAPSHOT_FILE_NAMEIDX_OOB");

        await repo.DisposeAsync();
        }

    [Fact]
    public async Task ValidateIntegrity_Errors_WhenErrorMessageStrIdxOutOfRange()
    {
        var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

        var sync = GetSyncObject(repo);
        var roots = GetScanRoots(repo);
        var snaps = GetScanRootSnapshots(repo);

        lock (sync)
        {
            roots.Clear();
            snaps.Clear();

            roots[1] = MakeRoot(rootId: 1, rootPath: "/r", dirId: 10);

            var pool = Pool("root"); // Count=1
            snaps[1] = MakeSnapshot(
                scanRootId: 1,
                pool: pool,
                dirs:
                [
                    new DirRecordV2
                    {
                        DirId = 10,
                        ParentDirId = 0,
                        NameStrIdx = 0,
                        ErrorMessageStrIdx = 5, // OOB (>=0 means present)
                        Status = ScanEntryStatus.Error
                    }
                ],
                files:
                [
                    new FileRecordV2
                    {
                        FileId = 1,
                        DirId = 10,
                        NameStrIdx = 0,
                        ErrorMessageStrIdx = 123, // OOB
                        Size = 1,
                        Status = ScanEntryStatus.Error
        }
                ]);
        }

        var issues = repo.ValidateIntegrity(ct: TestContext.Current.CancellationToken);

        Assert.Contains(issues, i => i.Code is "DIR_ERRIDX_OOB" or "ROOT_SNAPSHOT_DIR_ERRIDX_OOB");
        Assert.Contains(issues, i => i.Code is "FILE_ERRIDX_OOB" or "ROOT_SNAPSHOT_FILE_ERRIDX_OOB");

        await repo.DisposeAsync();
    }
}