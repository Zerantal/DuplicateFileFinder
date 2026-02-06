using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Core;

// ReSharper disable once InconsistentNaming
public sealed class Repo_ScanLifecycleTests
{
    [Fact]
    public async Task BeginNewScanAsync_CreatesScanRootAndRun_PersistsMeta()
    {
        var repoDir = CreateTempDir();
        try
        {
            await using var repo = await Repo.OpenAsync(repoDir, CancellationToken.None);
            var internalRepo = (IRepoInternal)repo;

            var ctx = await internalRepo.BeginNewScanAsync(
                rootPath: Path.Combine(repoDir, "root"),
                options: new ScanOptions(StartFresh: true),
                volumeInfo: null,
                ct: CancellationToken.None);

            Assert.True(ctx.ScanRoot.RootId > 0);
            Assert.True(ctx.ScanRoot.DirId > 0);

            Assert.Equal(ScanRunStatus.InProgress, ctx.Run.Status);
            Assert.True(ctx.Run.ScanSequence >= 0);

            // Public views reflect creation
            Assert.Contains(repo.ScanRootsView, r => r.RootId == ctx.ScanRoot.RootId && !r.IsDeleted);
            Assert.Contains(repo.ScanRunsView, r => r.ScanSequence == ctx.Run.ScanSequence && r.Status == ScanRunStatus.InProgress);
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    [Fact]
    public async Task CommitScanRootSnapshotV2Async_PublishesScanRootSnapshotCommittedEvent_WithSnapshotView()
    {
        var repoDir = CreateTempDir();
        try
        {
            await using var repo = await Repo.OpenAsync(repoDir, CancellationToken.None);
            var internalRepo = (IRepoInternal)repo;

            var sink = new CapturingSink();
            repo.RegisterEventSinkWithBootstrap(sink);
            sink.Drain(); // ignore bootstrap

            var ctx = await internalRepo.BeginNewScanAsync(
                rootPath: Path.Combine(repoDir, "root"),
                options: new ScanOptions(StartFresh: true),
                volumeInfo: null,
                ct: CancellationToken.None);

            var snap = MinimalSnapshot(ctx.ScanRoot.RootId);
            await internalRepo.CommitScanRootSnapshotV2Async(snap, CancellationToken.None);

            Assert.True(sink.TryDequeue(out var evt, TimeSpan.FromSeconds(2)));
            var committed = Assert.IsType<ScanRootSnapshotReplacedEvent>(evt);

            Assert.Equal(ctx.ScanRoot.RootId, committed.ScanRootId);
            Assert.NotNull(committed.RepoSnapshotView);
            Assert.True(committed.RepoSnapshotView.Snapshots.ContainsKey(ctx.ScanRoot.RootId));

            // And repo now serves it
            Assert.NotNull(repo.TryGetScanRootView(ctx.ScanRoot.RootId));
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    [Fact]
    public async Task MarkScanCompletedAsync_UpdatesRunAndPublishesFinalisedEvent()
    {
        var repoDir = CreateTempDir();
        try
        {
            await using var repo = await Repo.OpenAsync(repoDir, CancellationToken.None);
            var internalRepo = (IRepoInternal)repo;

            var sink = new CapturingSink();
            repo.RegisterEventSinkWithBootstrap(sink);
            sink.Drain();

            var ctx = await internalRepo.BeginNewScanAsync(
                rootPath: Path.Combine(repoDir, "root"),
                options: new ScanOptions(StartFresh: true),
                volumeInfo: null,
                ct: CancellationToken.None);

            await internalRepo.MarkScanCompletedAsync(ctx.Run.ScanSequence, CancellationToken.None);

            Assert.True(sink.TryDequeue(out var evt, TimeSpan.FromSeconds(2)));
            var fin = Assert.IsType<ScanRunFinalisedEvent>(evt);

            Assert.Equal(ctx.Run.ScanSequence, fin.Run.ScanSequence);
            Assert.Equal(ScanRunStatus.Completed, fin.Run.Status);
            Assert.NotNull(fin.Run.FinishedAt);

            // Public view updated
            var run = Assert.Single(repo.ScanRunsView, r => r.ScanSequence == ctx.Run.ScanSequence);
            Assert.Equal(ScanRunStatus.Completed, run.Status);
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    private static ScanRootSnapshotV2 MinimalSnapshot(ScanRootId scanRootId)
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
        var dir = Path.Combine(Path.GetTempPath(), "dff_repo_scanlife_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        { Directory.Delete(dir, recursive: true); }
        catch { /* ignore */ }
    }

    private sealed class CapturingSink : IRepoEventSink
    {
        private readonly ConcurrentQueue<RepoEvent> _queue = new();

        public void Post(RepoEvent evt) => _queue.Enqueue(evt);

        public void Drain()
        {
            while (_queue.TryDequeue(out _))
            { }
        }

        public bool TryDequeue(out RepoEvent evt, TimeSpan timeout)
        {
            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < timeout)
            {
                if (_queue.TryDequeue(out evt!))
                    return true;

                Thread.Sleep(5);
            }

            evt = null!;
            return false;
        }
    }
}

