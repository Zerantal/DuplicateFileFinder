using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Core;

// ReSharper disable once InconsistentNaming
public sealed class Repo_EventingTests
{
    [Fact]
    public async Task RegisterEventSinkWithBootstrap_PostsBootstrapImmediately_WithCoherentSnapshotView()
    {
        var repoDir = CreateTempDir();
        try
        {
            await using var repo = await Repo.OpenAsync(repoDir, CancellationToken.None);

            var sink = new CapturingSink();

            repo.RegisterEventSinkWithBootstrap(sink);

            Assert.True(sink.TryDequeue(out var evt, TimeSpan.FromSeconds(2)));
            var bootstrap = Assert.IsType<BootstrapEvent>(evt);

            Assert.NotNull(bootstrap.RepoSnapshotView);
            Assert.NotNull(bootstrap.RepoSnapshotView.Snapshots);
            Assert.NotNull(bootstrap.RepoSnapshotView.ScanRoots);

            // New repo => empty
            Assert.Empty(bootstrap.RepoSnapshotView.Snapshots);
            Assert.Empty(bootstrap.RepoSnapshotView.ScanRoots);
        }
        finally
        {
            TryDeleteDir(repoDir);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dff_repo_eventing_" + Guid.NewGuid());
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

