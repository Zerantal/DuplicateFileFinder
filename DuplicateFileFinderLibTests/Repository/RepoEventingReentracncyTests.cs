// DuplicateFileFinderLibTests/Repository/RepoEventingReentrancyTests.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository
{
    public sealed class RepoEventingReentrancyTests : IDisposable
    {
        private readonly string _repoDir;

        public RepoEventingReentrancyTests()
        {
            _repoDir = Path.Combine(
                Path.GetTempPath(),
                "dff-repo-eventing-tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_repoDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_repoDir))
                    Directory.Delete(_repoDir, recursive: true);
            }
            catch
            {
                // ignore cleanup errors in tests
            }
        }

        /// <summary>
        /// A sink that, for each event, tries to call _repo.GetSnapshot()
        /// from another thread and waits for it to complete.
        ///
        /// If the event is delivered while _sync is held, the other thread
        /// will block on _sync and we will time out.
        /// </summary>
        private sealed class ReentrantProbeSink : IRepoEventSink
        {
            private readonly Repo _repo;
            private readonly TimeSpan _timeout;

            public List<RepoEvent> Events { get; } = [];

            public ReentrantProbeSink(Repo repo, TimeSpan timeout)
            {
                _repo    = repo;
                _timeout = timeout;
            }

            public void Post(RepoEvent evt)
            {
                Events.Add(evt);

                using var gate = new ManualResetEventSlim(false);
                Exception? workerException = null;

                Task.Run(() =>
                {
                    try
                    {
                        // This takes _sync inside Repo.
                        var snapshot = _repo.GetSnapshot();
                        // Sanity: snapshot collections non-null.
                        Assert.NotNull(snapshot.Dirs);
                        Assert.NotNull(snapshot.Files);
                    }
                    catch (Exception ex)
                    {
                        workerException = ex;
                    }
                    finally
                    {
                        // ReSharper disable once AccessToDisposedClosure
                        gate.Set();
                    }
                });

                if (!gate.Wait(_timeout))
                {
                    throw new TimeoutException(
                        "Re-entrant GetSnapshot could not acquire repo lock in time; " +
                        "event was probably published while holding the repo lock.");
                }

                if (workerException is not null)
                    throw workerException;
            }
        }

        [Fact]
        public async Task RegisterEventSinkWithBootstrap_DeliversBootstrapEventOutsideLock()
        {
            var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

            // Seed repo with a tiny delta so bootstrap has some content to snapshot.
            var dirId = repo.AllocateDirId();
            var fileId = repo.AllocateFileId();
            var now = DateTimeOffset.UtcNow;

            var seedDelta = new RepoDelta
            {
                ScanSequence = 1,
                Dirs =
                [
                    new DirRecord
                    {
                        DirId                = dirId,
                        ParentDirId          = null,
                        Name                 = "root",
                        LastSeenScanSequence = 1,
                        Status               = ScanEntryStatus.Enumerated
                    }
                ],
                Files =
                [
                    new FileRecord
                    {
                        FileId               = fileId,
                        DirId                = dirId,
                        Name                 = "seed.dat",
                        Size                 = 123,
                        Created              = now,
                        Modified             = now,
                        Status               = ScanEntryStatus.Enumerated
                    }
                ]
            };

            await repo.CommitDeltaAsync(seedDelta, TestContext.Current.CancellationToken);

            var sink = new ReentrantProbeSink(repo, timeout: TimeSpan.FromSeconds(2));

            // Act: registering the sink should synchronously deliver a bootstrap event.
            repo.RegisterEventSinkWithBootstrap(sink);

            // Assert: we got at least one event, and the probe did not time out.
            Assert.NotEmpty(sink.Events);

            Assert.Contains(sink.Events, e => e is BootstrapEvent);
            

            await repo.DisposeAsync();
        }

        [Fact]
        public async Task CommitDeltaAsync_DeliversDeltaCommittedEventOutsideLock()
        {
            var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

            var sink = new ReentrantProbeSink(repo, timeout: TimeSpan.FromSeconds(2));
            repo.RegisterEventSinkWithBootstrap(sink); // may deliver a bootstrap, that's fine

            // Clear any bootstrap events so we can focus on the commit event
            sink.Events.Clear();

            // Create a simple delta
            var dirId = repo.AllocateDirId();
            var fileId = repo.AllocateFileId();
            var now = DateTimeOffset.UtcNow;

            var delta = new RepoDelta
            {
                ScanSequence = 2,
                Dirs =
                [
                    new DirRecord
                    {
                        DirId                = dirId,
                        ParentDirId          = null,
                        Name                 = "dir-commit",
                        LastSeenScanSequence = 2,
                        Status               = ScanEntryStatus.Enumerated
                    }
                ],
                Files =
                [
                    new FileRecord
                    {
                        FileId               = fileId,
                        DirId                = dirId,
                        Name                 = "file-commit.dat",
                        Size                 = 456,
                        Created              = now,
                        Modified             = now,
                        Status               = ScanEntryStatus.Enumerated
                    }
                ]
            };

            // Act: this should publish a DeltaCommitted event.
            await repo.CommitDeltaAsync(delta, TestContext.Current.CancellationToken);

            // Assert: our sink ran without timing out, meaning it could acquire the lock
            // from a different thread while handling the event.
            Assert.NotEmpty(sink.Events);
            
            Assert.Contains(sink.Events, e => e is DeltaCommittedEvent);
            
            await repo.DisposeAsync();
        }
    }
}
