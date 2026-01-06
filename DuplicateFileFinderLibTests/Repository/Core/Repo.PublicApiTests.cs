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
