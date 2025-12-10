// DuplicateFileFinderLibTests/Repository/RepoCommitCancellationTests.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Models;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository
{
    public sealed class RepoCommitCancellationTests : IDisposable
    {
        private readonly string _repoDir;

        public RepoCommitCancellationTests()
        {
            _repoDir = Path.Combine(
                Path.GetTempPath(),
                "dff-repo-commit-cancel-tests",
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
                // ignore cleanup errors
            }
        }

        [Fact]
        public async Task CommitDeltaAsync_WithCancelledToken_DoesNotApplyDeltaOrCreateLogFile()
        {
            // Open fresh repo
            var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
            var now  = DateTimeOffset.UtcNow;

            // Seed with a small baseline delta so snapshot/logs are non-empty
            var baseDirId  = repo.AllocateDirId();
            var baseFileId = repo.AllocateFileId();

            var baseDelta = new RepoDelta
            {
                ScanSequence = 1,
                Dirs =
                [
                    new DirRecord
                    {
                        DirId                = baseDirId,
                        ParentDirId          = null,
                        Name                 = "baseline-dir",
                        LastSeenScanSequence = 1,
                        Status               = ScanEntryStatus.Enumerated
                    }
                ],
                Files =
                [
                    new FileRecord
                    {
                        FileId               = baseFileId,
                        DirId                = baseDirId,
                        Name                 = "baseline.dat",
                        Size                 = 123,
                        Created              = now,
                        Modified             = now,
                        LastSeenScanSequence = 1,
                        Status               = ScanEntryStatus.Enumerated
                    }
                ]
            };

            await repo.CommitDeltaAsync(baseDelta, TestContext.Current.CancellationToken);

            var snapshotBefore   = repo.GetRepoView();
            var logDir           = Path.Combine(_repoDir, "log");
            var logFilesBefore   = Directory.Exists(logDir)
                ? Directory.GetFiles(logDir, "*.delta").OrderBy(p => p).ToArray()
                : [];
            var nextLogSeqBefore = repo.Meta.NextLogSequence;

            // Prepare a new delta that we will attempt to commit with a cancelled token
            var newDirId  = repo.AllocateDirId();
            var newFileId = repo.AllocateFileId();

            var cancelledDelta = new RepoDelta
            {
                ScanSequence = 2,
                Dirs =
                [
                    new DirRecord
                    {
                        DirId                = newDirId,
                        ParentDirId          = null,
                        Name                 = "should-not-appear-dir",
                        LastSeenScanSequence = 2,
                        Status               = ScanEntryStatus.Enumerated
                    }
                ],
                Files =
                [
                    new FileRecord
                    {
                        FileId               = newFileId,
                        DirId                = newDirId,
                        Name                 = "should-not-appear.dat",
                        Size                 = 999,
                        Created              = now,
                        Modified             = now,
                        LastSeenScanSequence = 2,
                        Status               = ScanEntryStatus.Enumerated
                    }
                ]
            };

            // Cancellation token already cancelled before WriteAllBytesAsync is called
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act: CommitDeltaAsync should observe the cancelled token and throw
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repo.CommitDeltaAsync(cancelledDelta, cts.Token));

            // Assert: no new dirs/files applied
            var snapshotAfter = repo.GetRepoView();

            // Same key sets for existing dirs/files
            Assert.True(new HashSet<long>(snapshotBefore.Dirs.Keys).SetEquals(snapshotAfter.Dirs.Keys));
            Assert.True(new HashSet<long>(snapshotBefore.Files.Keys).SetEquals(snapshotAfter.Files.Keys));

            // The new IDs must not be present
            Assert.DoesNotContain(newDirId, snapshotAfter.Dirs.Keys);
            Assert.DoesNotContain(newFileId, snapshotAfter.Files.Keys);

            // And existing entries remain unchanged (record equality)
            foreach (var (id, beforeDir) in snapshotBefore.Dirs)
            {
                Assert.True(snapshotAfter.Dirs.TryGetValue(id, out var afterDir));
                Assert.Equal(beforeDir, afterDir);
            }

            foreach (var (id, beforeFile) in snapshotBefore.Files)
            {
                Assert.True(snapshotAfter.Files.TryGetValue(id, out var afterFile));
                Assert.Equal(beforeFile, afterFile);
            }

            // Assert: no new .delta log file created
            var logFilesAfter = Directory.Exists(logDir)
                ? Directory.GetFiles(logDir, "*.delta").OrderBy(p => p).ToArray()
                : [];

            Assert.Equal(logFilesBefore, logFilesAfter);

            // Note: AllocateLogId() is called before the write, so NextLogSequence
            // will typically have advanced by 1 even though no log file exists.
            Assert.Equal(nextLogSeqBefore + 1, repo.Meta.NextLogSequence);

            // Finally, ensure we can reopen the repo and get the same state
            await repo.DisposeAsync();

            var reopened = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
            var snapshotReopened = reopened.GetRepoView();

            Assert.True(new HashSet<long>(snapshotBefore.Dirs.Keys).SetEquals(snapshotReopened.Dirs.Keys));
            Assert.True(new HashSet<long>(snapshotBefore.Files.Keys).SetEquals(snapshotReopened.Files.Keys));

            foreach (var (id, beforeDir) in snapshotBefore.Dirs)
            {
                Assert.True(snapshotReopened.Dirs.TryGetValue(id, out var afterDir));
                Assert.Equal(beforeDir, afterDir);
            }

            foreach (var (id, beforeFile) in snapshotBefore.Files)
            {
                Assert.True(snapshotReopened.Files.TryGetValue(id, out var afterFile));
                Assert.Equal(beforeFile, afterFile);
            }

            await reopened.DisposeAsync();
        }
    }
}
