using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage;
using DuplicateFileFinderLib.Repository.Storage.Models;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Core;

// ReSharper disable once InconsistentNaming
public sealed class Repo_PersistenceTests
{
    [Fact]
    public async Task PersistMetaIfDirtyAsync_IsTriggeredByBeginScan_AndMetaFileUpdatesOnDisk()
    {
        var repoDir = CreateTempDir();
        try
        {
            await using var repo = await Repo.OpenAsync(repoDir, CancellationToken.None);
            var internalRepo = (IRepoInternal)repo;

            // Before any ops: meta exists
            var meta0 = await RepoStore.LoadMetaAsync(repoDir, CancellationToken.None);
            Assert.NotNull(meta0);
            var runs0 = meta0.ScanRuns.Count;

            // BeginScan causes MarkMetaDirty + PersistMetaIfDirtyAsync in BeginScanAsync
            _ = await internalRepo.BeginScanAsync(
                rootPath: Path.Combine(repoDir, "root"),
                options: new ScanOptions(StartFresh: true),
                volumeInfo: null,
                ct: CancellationToken.None);

            var meta1 = await RepoStore.LoadMetaAsync(repoDir, CancellationToken.None);
            Assert.NotNull(meta1);
            Assert.True(meta1.ScanRuns.Count == runs0 + 1);
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    [Fact]
    public async Task PersistScanRootSnapshotV2Async_WritesSnapshotFile_AndRepoCanReloadIt()
    {
        var repoDir = CreateTempDir();
        try
        {
            long rootId;

            // Create repo + root + snapshot
            await using (var repo = await Repo.OpenAsync(repoDir, CancellationToken.None))
            {
                var internalRepo = (IRepoInternal)repo;

                var ctx = await internalRepo.BeginScanAsync(
                    rootPath: Path.Combine(repoDir, "root"),
                    options: new ScanOptions(StartFresh: true),
                    volumeInfo: null,
                    ct: CancellationToken.None);

                rootId = ctx.ScanRoot.RootId;

                var snap = MinimalSnapshot(rootId);
                await internalRepo.CommitScanRootSnapshotV2Async(snap, CancellationToken.None);

                // Snapshot file exists
                var snapPath = Path.Combine(repoDir, "roots", $"{rootId}.mp");
                Assert.True(File.Exists(snapPath));
            }

            // Re-open repo; snapshot should be loaded from store
            await using (var repo2 = await Repo.OpenAsync(repoDir, CancellationToken.None))
            {
                var view = repo2.TryGetScanRootView(rootId);
                Assert.NotNull(view);
                Assert.Single(view.Dirs);
            }
        }
        finally
        {
            TryDeleteDir(repoDir);
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
            Files = Array.Empty<FileRecordV2>()
        };
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dff_repo_persist_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
    }
}

