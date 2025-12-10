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
    public sealed class RepoCompactionCancellationTests : IDisposable
    {
        private readonly string _repoDir;

        public RepoCompactionCancellationTests()
        {
            _repoDir = Path.Combine(
                Path.GetTempPath(),
                "dff-repo-compaction-cancel-tests",
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
        public async Task CompactAsync_WithPreCancelledToken_DoesNotAdvanceMetaOrDeleteLogs()
        {
            // Arrange: open repo and seed with a scan root + one delta
            var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
            var now  = DateTimeOffset.UtcNow;

            // Create at least one ScanRoot so the root loop in CompactAsync runs.
            // We don't care about the operation kind; default is fine.
            repo.BeginScan(
                rootPath: "/fake/root",
                scanOperation: default,
                volumeInfo: null,
                maxFilesBeforeFlush: 10,
                maxDirsBeforeFlush: 10);
            

            // Seed a single delta so we have at least one log file.
            var dirId  = repo.AllocateDirId();
            var fileId = repo.AllocateFileId();

            var delta = new RepoDelta
            {
                ScanSequence = 1,
                Dirs =
                [
                    new DirRecord
                    {
                        DirId                = dirId,
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
                        FileId               = fileId,
                        DirId                = dirId,
                        Name                 = "baseline.dat",
                        Size                 = 123,
                        Created              = now,
                        Modified             = now,
                        LastSeenScanSequence = 1,
                        Status               = ScanEntryStatus.Enumerated
                    }
                ]
            };

            await repo.CommitDeltaAsync(delta, TestContext.Current.CancellationToken);

            // Snapshot state before compaction
            var snapshotBefore = repo.GetRepoView();

            var metaBefore = repo.Meta;
            var generationBefore           = metaBefore.Generation;
            var lastSnapshottedBefore      = metaBefore.LastSnapshottedLogSequence;
            var lastCompactionBefore       = metaBefore.LastCompaction;
            var nextLogSeqBefore           = metaBefore.NextLogSequence;

            var logDir = Path.Combine(_repoDir, "log");
            var logFilesBefore = Directory.Exists(logDir)
                ? Directory.GetFiles(logDir, "*.delta").OrderBy(p => p).ToArray()
                : [];

            // Use a token that is already cancelled before CompactAsync starts.
            // This will cause ct.ThrowIfCancellationRequested() to trigger on
            // the first root iteration, BEFORE meta/log advancement.
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act: CompactAsync should throw due to cancellation
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => repo.CompactAsync(policy: null, ct: cts.Token));

            // Assert: meta fields have not advanced
            var metaAfter = repo.Meta;
            Assert.Equal(generationBefore,      metaAfter.Generation);
            Assert.Equal(lastSnapshottedBefore, metaAfter.LastSnapshottedLogSequence);
            Assert.Equal(lastCompactionBefore,  metaAfter.LastCompaction);
            Assert.Equal(nextLogSeqBefore,      metaAfter.NextLogSequence);

            // Assert: no .delta files were deleted
            var logFilesAfter = Directory.Exists(logDir)
                ? Directory.GetFiles(logDir, "*.delta").OrderBy(p => p).ToArray()
                : [];

            Assert.Equal(logFilesBefore, logFilesAfter);

            // Assert: repo state is unchanged in-memory
            var snapshotAfter = repo.GetRepoView();
            Assert.True(new HashSet<long>(snapshotBefore.Dirs.Keys).SetEquals(snapshotAfter.Dirs.Keys));
            Assert.True(new HashSet<long>(snapshotBefore.Files.Keys).SetEquals(snapshotAfter.Files.Keys));

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

            // And the repo remains reopenable with the same state
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