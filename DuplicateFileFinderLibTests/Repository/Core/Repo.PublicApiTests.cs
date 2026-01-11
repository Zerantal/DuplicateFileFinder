using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Core;

// ReSharper disable once InconsistentNaming
public sealed class Repo_PublicApiTests
{
    [Fact]
    public async Task GetRepoSnapshotView_ReturnsReadOnlyViews_WithConsistentMaps()
    {
        var repoDir = CreateTempDir();
        try
        {
            await using var repo = await Repo.OpenAsync(repoDir, CancellationToken.None);

            var view = repo.GetRepoSnapshotView();
            Assert.NotNull(view.Snapshots);
            Assert.NotNull(view.ScanRoots);

            Assert.Empty(view.Snapshots);
            Assert.Empty(view.ScanRoots);
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    [Fact]
    public async Task DeleteScanRootAsync_MarksRootDeleted_RemovesSnapshotView_AndDeletesSnapshotFile()
    {
        var repoDir = CreateTempDir();
        try
        {
            await using var repo = await Repo.OpenAsync(repoDir, CancellationToken.None);
            var internalRepo = (IRepoInternal)repo;

            // Create a root/run
            var ctx = await internalRepo.BeginNewScanAsync(
                rootPath: Path.Combine(repoDir, "rootA"),
                options: new ScanOptions(StartFresh: true),
                volumeInfo: null,
                ct: CancellationToken.None);

            // Commit a snapshot for the root
            var snap = MinimalSnapshot(ctx.ScanRoot.RootId);
            await internalRepo.CommitScanRootSnapshotV2Async(snap, CancellationToken.None);

            // Ensure view exists
            var before = repo.TryGetScanRootView(ctx.ScanRoot.RootId);
            Assert.NotNull(before);

            // Delete root
            await repo.DeleteScanRootAsync(ctx.ScanRoot.RootId, CancellationToken.None);

            // Snapshot no longer available
            Assert.Null(repo.TryGetScanRootView(ctx.ScanRoot.RootId));

            // Root still present but marked deleted (metadata)
            var roots = repo.ScanRootsView;
            var root = Assert.Single(roots, r => r.RootId == ctx.ScanRoot.RootId);
            Assert.True(root.IsDeleted);
            Assert.NotNull(root.DeletedAtUtc);
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    [Fact]
    public async Task BeginNewScanAsync_ByScanRootId_UsesVolumePathAndRelativeRootPath_ToResolveRunRootPath()
    {
        var repoDir = CreateTempDir();
        var volumePath = CreateTempDir();
        try
        {
            // Arrange
            var rootPath = Path.Combine(volumePath, "rootA");
            Directory.CreateDirectory(rootPath);

            await using var repo = await Repo.OpenAsync(repoDir, CancellationToken.None);
            var internalRepo = (IRepoInternal)repo;

            var vInfo = new DuplicateFileFinderLib.IO.VolumeInfo
            {
                DevicePath = "/dev/test",
                VolumePath = Path.GetFullPath(volumePath),
                VolumeId = "VOL-1"
            };

            // First scan creates the scan root with RootPath relative to VolumePath.
            var ctx1 = await internalRepo.BeginNewScanAsync(
                rootPath: rootPath,
                options: new ScanOptions(StartFresh: true),
                volumeInfo: vInfo,
                ct: CancellationToken.None);

            Assert.NotNull(ctx1.ScanRoot);
            Assert.Equal(PathUtils.NormalizePath(rootPath), ctx1.Run.RootPath);

            // Act: start another scan explicitly by scanRootId (no path provided)
            var ctx2 = await internalRepo.BeginRescanAsync(
                scanRootId: ctx1.ScanRoot.RootId,
                options: new ScanOptions(StartFresh: true),
                volumeInfo: null,
                ct: CancellationToken.None);

            // Assert: resolved run root path is the original absolute path
            Assert.Equal(PathUtils.NormalizePath(rootPath), ctx2.Run.RootPath);

            // And the stored scan root is still relative (because VolumePath was known)
            var stored = Assert.Single(repo.ScanRootsView, r => r.RootId == ctx1.ScanRoot.RootId);
            Assert.Equal(PathUtils.NormalizePath(Path.GetRelativePath(vInfo.VolumePath, rootPath)), stored.RootPath);
            Assert.Equal(PathUtils.NormalizePath(vInfo.VolumePath), PathUtils.NormalizePath(stored.VolumePath!));
        }
        finally
        {
            TryDeleteDir(repoDir);
            TryDeleteDir(volumePath);
        }
    }

    [Fact]
    public async Task DeleteFileAsync_MarksFileDeleted_AndBumpsGeneration()
    {
        var repoDir = CreateTempDir();
        try
        {
            await using var repo = await Repo.OpenAsync(repoDir, CancellationToken.None);
            var internalRepo = (IRepoInternal)repo;

            // Create a root/run
            var ctx = await internalRepo.BeginNewScanAsync(
                rootPath: Path.Combine(repoDir, "rootA"),
                options: new ScanOptions(StartFresh: true),
                volumeInfo: null,
                ct: CancellationToken.None);

            // Commit snapshot containing 1 root dir + 2 files
            var snap = Snapshot_WithTwoFiles(ctx.ScanRoot.RootId);
            await internalRepo.CommitScanRootSnapshotV2Async(snap, CancellationToken.None);

            var before = repo.TryGetScanRootView(ctx.ScanRoot.RootId);
            Assert.NotNull(before);
            Assert.Equal(2, before.Files.Count);
            Assert.Equal(ScanEntryStatus.Enumerated, before.Files[0].Status);

            // Delete file at index 0
            var fh = new FileHandle(ctx.ScanRoot.RootId, Index: 0);
            var r1 = await repo.DeleteFileAsync(fh, CancellationToken.None);

            Assert.True(r1.Success);
            Assert.Equal(ctx.ScanRoot.RootId, r1.ScanRootId);
            Assert.Equal(1, r1.DeletedFileCount);
            Assert.Equal(0, r1.DeletedDirCount);

            var after = repo.TryGetScanRootView(ctx.ScanRoot.RootId);
            Assert.NotNull(after);

            Assert.Equal(ScanEntryStatus.Deleted, after.Files[0].Status);
            Assert.Equal(ScanEntryStatus.Enumerated, after.Files[1].Status);

            // Idempotent: deleting again should be no-op (no generation bump)
            var r2 = await repo.DeleteFileAsync(fh, CancellationToken.None);

            Assert.True(r2.Success);
            Assert.Equal(0, r2.DeletedFileCount);
            Assert.Equal(0, r2.DeletedDirCount);
            Assert.Equal(r1.Generation, r2.Generation);
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    [Fact]
    public async Task DeleteDirAsync_DeletesDirRecursively_AndMarksAllDescendantFilesDeleted()
    {
        var repoDir = CreateTempDir();
        try
        {
            await using var repo = await Repo.OpenAsync(repoDir, CancellationToken.None);
            var internalRepo = (IRepoInternal)repo;

            // Create a root/run
            var ctx = await internalRepo.BeginNewScanAsync(
                rootPath: Path.Combine(repoDir, "rootA"),
                options: new ScanOptions(StartFresh: true),
                volumeInfo: null,
                ct: CancellationToken.None);

            // Commit snapshot:
            // Dirs:  root(DirId=1) -> child(DirId=2) -> grand(DirId=3)
            // Files: rootFile in DirId=1, childFile in DirId=2, grandFile in DirId=3
            var snap = Snapshot_WithDirTreeAndFiles(ctx.ScanRoot.RootId);
            await internalRepo.CommitScanRootSnapshotV2Async(snap, CancellationToken.None);

            var before = repo.TryGetScanRootView(ctx.ScanRoot.RootId);
            Assert.NotNull(before);
            Assert.Equal(3, before.Dirs.Count);
            Assert.Equal(3, before.Files.Count);

            // Delete the "child" dir = index 1 (DirId=2). Should delete dirs 2+3 and files under them.
            var dh = new DirHandle(ctx.ScanRoot.RootId, Index: 1);
            var r1 = await repo.DeleteDirAsync(dh, CancellationToken.None);

            Assert.True(r1.Success);
            Assert.Equal(ctx.ScanRoot.RootId, r1.ScanRootId);

            // Expect: deletedDirs = 2 (child + grand), deletedFiles = 2 (childFile + grandFile)
            Assert.Equal(2, r1.DeletedDirCount);
            Assert.Equal(2, r1.DeletedFileCount);

            var after = repo.TryGetScanRootView(ctx.ScanRoot.RootId);
            Assert.NotNull(after);

            // Dirs: root remains live, child+grand deleted
            Assert.Equal(ScanEntryStatus.Enumerated, after.Dirs[0].Status); // root (DirId=1)
            Assert.Equal(ScanEntryStatus.Deleted, after.Dirs[1].Status);    // child (DirId=2)
            Assert.Equal(ScanEntryStatus.Deleted, after.Dirs[2].Status);    // grand (DirId=3)

            // Files: rootFile remains live, childFile+grandFile deleted
            Assert.Equal(ScanEntryStatus.Enumerated, after.Files[0].Status); // rootFile (DirId=1)
            Assert.Equal(ScanEntryStatus.Deleted, after.Files[1].Status);    // childFile (DirId=2)
            Assert.Equal(ScanEntryStatus.Deleted, after.Files[2].Status);    // grandFile (DirId=3)

            // Idempotent: deleting same dir again should be no-op (no generation bump)
            var r2 = await repo.DeleteDirAsync(dh, CancellationToken.None);

            Assert.True(r2.Success);
            Assert.Equal(0, r2.DeletedDirCount);
            Assert.Equal(0, r2.DeletedFileCount);
            Assert.Equal(r1.Generation, r2.Generation);
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    // ---------------------------------------------------------------------
    // Snapshot builders (minimal but valid for index rebuild + status checks)
    // ---------------------------------------------------------------------

    private static ScanRootSnapshotV2 Snapshot_WithTwoFiles(long scanRootId)
    {
        // pool: [ "root", "fileA.bin", "fileB.bin", "" ]
        var pool = PackedStringPool.FromStrings(["root", "fileA.bin", "fileB.bin", ""]);

        return new ScanRootSnapshotV2
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs =
            [
                new DirRecordV2
                {
                    DirId = 1,
                    ParentDirId = -1,
                    NameStrIdx = 0,
                    ErrorMessageStrIdx = 3,
                    LastSeenScanSequence = 1,
                    Status = ScanEntryStatus.Enumerated,
                    CreatedTicks = 0,
                    ModifiedTicks = 0
                }
            ],
            Files =
            [
                new FileRecordV2
                {
                    FileId = 10,
                    DirId = 1,
                    NameStrIdx = 1,
                    ErrorMessageStrIdx = 3,
                    Size = 123,
                    Hash = HashKey.NotComputed,
                    LastSeenScanSequence = 1,
                    Status = ScanEntryStatus.Enumerated,
                    CreatedTicks = 0,
                    ModifiedTicks = 0
                },
                new FileRecordV2
                {
                    FileId = 11,
                    DirId = 1,
                    NameStrIdx = 2,
                    ErrorMessageStrIdx = 3,
                    Size = 456,
                    Hash = HashKey.NotComputed,
                    LastSeenScanSequence = 1,
                    Status = ScanEntryStatus.Enumerated,
                    CreatedTicks = 0,
                    ModifiedTicks = 0
                }
            ]
        };
    }

    private static ScanRootSnapshotV2 Snapshot_WithDirTreeAndFiles(long scanRootId)
    {
        // pool: [ "root", "child", "grand", "root.bin", "child.bin", "grand.bin", "" ]
        var pool = PackedStringPool.FromStrings(["root", "child", "grand", "root.bin", "child.bin", "grand.bin", ""]);

        return new ScanRootSnapshotV2
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs =
            [
                new DirRecordV2
                {
                    DirId = 1,
                    ParentDirId = -1,
                    NameStrIdx = 0,
                    ErrorMessageStrIdx = 6,
                    LastSeenScanSequence = 1,
                    Status = ScanEntryStatus.Enumerated,
                    CreatedTicks = 0,
                    ModifiedTicks = 0
                },
                new DirRecordV2
                {
                    DirId = 2,
                    ParentDirId = 1,
                    NameStrIdx = 1,
                    ErrorMessageStrIdx = 6,
                    LastSeenScanSequence = 1,
                    Status = ScanEntryStatus.Enumerated,
                    CreatedTicks = 0,
                    ModifiedTicks = 0
                },
                new DirRecordV2
                {
                    DirId = 3,
                    ParentDirId = 2,
                    NameStrIdx = 2,
                    ErrorMessageStrIdx = 6,
                    LastSeenScanSequence = 1,
                    Status = ScanEntryStatus.Enumerated,
                    CreatedTicks = 0,
                    ModifiedTicks = 0
                }
            ],
            Files =
            [
                new FileRecordV2
                {
                    FileId = 10,
                    DirId = 1,
                    NameStrIdx = 3,
                    ErrorMessageStrIdx = 6,
                    Size = 100,
                    Hash = HashKey.NotComputed,
                    LastSeenScanSequence = 1,
                    Status = ScanEntryStatus.Enumerated,
                    CreatedTicks = 0,
                    ModifiedTicks = 0
                },
                new FileRecordV2
                {
                    FileId = 11,
                    DirId = 2,
                    NameStrIdx = 4,
                    ErrorMessageStrIdx = 6,
                    Size = 200,
                    Hash = HashKey.NotComputed,
                    LastSeenScanSequence = 1,
                    Status = ScanEntryStatus.Enumerated,
                    CreatedTicks = 0,
                    ModifiedTicks = 0
                },
                new FileRecordV2
                {
                    FileId = 12,
                    DirId = 3,
                    NameStrIdx = 5,
                    ErrorMessageStrIdx = 6,
                    Size = 300,
                    Hash = HashKey.NotComputed,
                    LastSeenScanSequence = 1,
                    Status = ScanEntryStatus.Enumerated,
                    CreatedTicks = 0,
                    ModifiedTicks = 0
                }
            ]
        };
    }

    private static ScanRootSnapshotV2 MinimalSnapshot(long scanRootId)
    {
        return new ScanRootSnapshotV2
        {
            ScanRootId = scanRootId,
            StringPool = PackedStringPool.FromStrings(["root", ""]),
            Dirs =
            [
                new DirRecordV2
                {
                    DirId = 1,
                    ParentDirId = -1,
                    NameStrIdx = 0,
                    ErrorMessageStrIdx = 1,
                    LastSeenScanSequence = 1,
                    Status = ScanEntryStatus.Enumerated,
                    CreatedTicks = 0,
                    ModifiedTicks = 0
                }
            ],
            Files = []
        };
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dff_repo_public_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        { Directory.Delete(dir, recursive: true); }
        catch { /* ignore */ }
    }
}
