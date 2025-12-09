using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Models;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository;

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
            // ignore cleanup errors
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

    private static List<ScanRun> GetScanRuns(Repo repo)
    {
        var f = typeof(Repo).GetField("_scanRuns", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        return (List<ScanRun>)f.GetValue(repo)!;
    }

    // ====================================================================
    // 1. ValidateIntegrity: deleted roots are ignored in certain checks
    // ====================================================================

    [Fact]
    public async Task ValidateIntegrity_DoesNotWarnOnDeletedRoot_NoRuns_NoSnapshot()
    {
        var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

        // Create a scan root by doing a trivial scan
        var rootPath = Path.Combine(_repoDir, "root");
        Directory.CreateDirectory(rootPath);

        long rootId;
        await using (var session = repo.BeginScan(
                         rootPath,
                         default,
                         null,
                         10,
                         10))
        {
            rootId = repo.ScanRootsView.First().RootId;

            // Just complete the scan without adding anything
            await session.CompleteAsync(TestContext.Current.CancellationToken);
        }

        // Soft-delete the root via internal API
        repo.DeleteScanRoot(rootId);

        // We do NOT call SaveScanSnapshots, so there are no roots/*.bin files at all.
        // ValidateIntegrity should:
        // - NOT report ROOT_UNUSED_NO_RUNS for the deleted root
        // - NOT report ROOT_SNAPSHOT_MISSING for the deleted root
        // - NOT report ROOT_DUP_ROOTPATH (only one root path, and it's deleted)

        var issues = repo.ValidateIntegrity(false, TestContext.Current.CancellationToken);

        var codes = issues.Select(i => i.Code).ToList();

        Assert.DoesNotContain("ROOT_UNUSED_NO_RUNS", codes);
        Assert.DoesNotContain("ROOT_SNAPSHOT_MISSING", codes);
        Assert.DoesNotContain("ROOT_DUP_ROOTPATH", codes);

        await repo.DisposeAsync();
    }

    [Fact]
    public async Task ValidateIntegrity_WarnsOnDuplicateLiveRoots_ButNotWhenOneIsDeleted()
    {
        var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

        // We will directly manipulate _scanRoots to create impossible states
        // (that's exactly what integrity checks are for).
        var sync = GetSyncObject(repo);
        var scanRoots = GetScanRoots(repo);

        var commonPath = "/dup-root";

        // Case A: two live roots with same RootPath -> should produce ROOT_DUP_ROOTPATH
        lock (sync)
        {
            scanRoots.Clear();

            scanRoots[1] = new ScanRoot
            {
                RootId = 1,
                RootPath = commonPath,
                DirId = 0,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                LastScannedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                IsDeleted = false
            };

            scanRoots[2] = new ScanRoot
            {
                RootId = 2,
                RootPath = commonPath,
                DirId = 0,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastScannedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                IsDeleted = false
            };
        }

        var issuesA = repo.ValidateIntegrity(ct: TestContext.Current.CancellationToken);
        Assert.Contains(issuesA, i => i.Code == "ROOT_DUP_ROOTPATH");

        // Case B: one live, one deleted -> only one live root, no duplicate warning
        lock (sync)
        {
            scanRoots.Clear();

            scanRoots[1] = new ScanRoot
            {
                RootId = 1,
                RootPath = commonPath,
                DirId = 0,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                LastScannedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                IsDeleted = false
            };

            scanRoots[2] = new ScanRoot
            {
                RootId = 2,
                RootPath = commonPath,
                DirId = 0,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                LastScannedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                IsDeleted = true
            };
        }

        var issuesB = repo.ValidateIntegrity(ct: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(issuesB, i => i.Code == "ROOT_DUP_ROOTPATH");

        await repo.DisposeAsync();
    }

    // ====================================================================
    // 2. RepairMigratedRepo: keep deleted roots, dedupe only live roots
    // ====================================================================

    [Fact]
    public async Task RepairMigratedRepo_KeepsDeletedRootsWithNoRuns()
    {
        var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

        var sync = GetSyncObject(repo);
        var scanRoots = GetScanRoots(repo);
        var scanRuns = GetScanRuns(repo);

        lock (sync)
        {
            scanRoots.Clear();
            scanRuns.Clear();

            // Deleted root: should be preserved even with no runs
            scanRoots[1] = new ScanRoot
            {
                RootId = 1,
                RootPath = "/deleted-root",
                DirId = 0,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                LastScannedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                IsDeleted = true,
                DeletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-20)
            };

            // Legacy live root with no runs: should be removed by repair
            scanRoots[2] = new ScanRoot
            {
                RootId = 2,
                RootPath = "/orphan-root",
                DirId = 0,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                LastScannedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                IsDeleted = false
            };
        }

        // Act
        repo.RepairMigratedRepo();

        // Assert
        lock (sync)
        {
            scanRoots = GetScanRoots(repo); // re-read after repair

            Assert.Contains(1L, scanRoots.Keys); // deleted root kept
            Assert.DoesNotContain(2L, scanRoots.Keys); // orphan live root dropped

            var deletedRoot = scanRoots[1];
            Assert.True(deletedRoot.IsDeleted);
            Assert.Equal("/deleted-root", deletedRoot.RootPath);
        }

        await repo.DisposeAsync();
    }

    [Fact]
    public async Task RepairMigratedRepo_DeduplicatesLiveRootsOnly_AndKeepsDeletedRoots()
    {
        var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

        var sync = GetSyncObject(repo);
        var scanRoots = GetScanRoots(repo);
        var scanRuns = GetScanRuns(repo);

        var dirsField = typeof(Repo).GetField("_dirs", BindingFlags.Instance | BindingFlags.NonPublic);
        var filesField = typeof(Repo).GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(dirsField);
        Assert.NotNull(filesField);
        var dirs = (Dictionary<long, DirRecord>)dirsField.GetValue(repo)!;
        var files = (Dictionary<long, FileRecord>)filesField.GetValue(repo)!;

        var now = DateTimeOffset.UtcNow;

        lock (sync)
        {
            scanRoots.Clear();
            scanRuns.Clear();
            dirs.Clear();
            files.Clear();

            // Two live roots with same RootPath -> should be merged
            scanRoots[1] = new ScanRoot
            {
                RootId = 1,
                RootPath = "/dup-root",
                DirId = 100,
                CreatedAt = now.AddMinutes(-30),
                LastScannedAt = now.AddMinutes(-30),
                IsDeleted = false
            };

            scanRoots[2] = new ScanRoot
            {
                RootId = 2,
                RootPath = "/dup-root",
                DirId = 200,
                CreatedAt = now.AddMinutes(-20),
                LastScannedAt = now.AddMinutes(-5), // newer, should win
                IsDeleted = false
            };

            // Deleted root with same RootPath: should be left alone
            scanRoots[3] = new ScanRoot
            {
                RootId = 3,
                RootPath = "/dup-root",
                DirId = 0,
                CreatedAt = now.AddMinutes(-10),
                LastScannedAt = now.AddMinutes(-10),
                IsDeleted = true,
                DeletedAtUtc = now.AddMinutes(-1)
            };

            // Runs referencing both live roots; after repair they should be
            // remapped to the canonical live root (RootId=2).
            scanRuns.Add(new ScanRun
            {
                ScanSequence = 1,
                ScanRootId = 1,
                RootPath = "/dup-root",
                Status = ScanRunStatus.Completed,
                StartedAt = now.AddMinutes(-29),
                FinishedAt = now.AddMinutes(-28)
            });

            scanRuns.Add(new ScanRun
            {
                ScanSequence = 2,
                ScanRootId = 2,
                RootPath = "/dup-root",
                Status = ScanRunStatus.Completed,
                StartedAt = now.AddMinutes(-4),
                FinishedAt = now.AddMinutes(-3)
            });

            // Link dirs to runs via LastSeenScanSequence so they are not pruned in step 4
            dirs[100] = new DirRecord
            {
                DirId = 100,
                ParentDirId = null,
                Name = "dir-root-1",
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Enumerated
            };

            dirs[200] = new DirRecord
            {
                DirId = 200,
                ParentDirId = null,
                Name = "dir-root-2",
                LastSeenScanSequence = 2,
                Status = ScanEntryStatus.Enumerated
            };
        }

        // Act
        repo.RepairMigratedRepo();

        // Assert
        lock (sync)
        {
            scanRoots = GetScanRoots(repo);
            scanRuns = GetScanRuns(repo);

            // There should be exactly one live root for "/dup-root"
            var liveRoots = scanRoots.Values.Where(r => r is { IsDeleted: false, RootPath: "/dup-root" }).ToList();
            Assert.Single(liveRoots);
            var canonical = liveRoots[0];

            // Deleted root should still exist
            Assert.True(scanRoots.TryGetValue(3L, out var deleted));
            Assert.True(deleted.IsDeleted);
            Assert.Equal("/dup-root", deleted.RootPath);

            // All runs must point to the canonical live root
            Assert.All(scanRuns, r => Assert.Equal(canonical.RootId, r.ScanRootId));
        }

        await repo.DisposeAsync();
    }
}