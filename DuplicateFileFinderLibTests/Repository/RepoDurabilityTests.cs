using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Models;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository
{
    public sealed class RepoDurabilityTests : IDisposable
    {
        private readonly string _repoDir;

        public RepoDurabilityTests()
        {
            _repoDir = Path.Combine(
                Path.GetTempPath(),
                "dff-repo-durability-tests",
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
        public async Task DisposeAsync_ThenReopen_PreservesSnapshotAndMeta()
        {
            var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;

            // Seed multiple deltas to exercise replay and log handling
            const int deltaCount = 8;
            for (var i = 0; i < deltaCount; i++)
            {
                var dirId = repo.AllocateDirId();
                var fileId = repo.AllocateFileId();

                var dir = new DirRecord
                {
                    DirId = dirId,
                    ParentDirId = null,
                    Name = $"dir-{i}",
                    LastSeenScanSequence = i,
                    Status = ScanEntryStatus.Enumerated
                };

                var file = new FileRecord
                {
                    FileId = fileId,
                    DirId = dirId,
                    Name = $"file-{i}.dat",
                    Size = 100 + i,
                    Created = now,
                    Modified = now,
                    LastSeenScanSequence = i,
                    Status = ScanEntryStatus.Enumerated
                };

                var delta = new RepoDelta
                {
                    ScanSequence = i,
                    Dirs = [dir],
                    Files = [file]
                };

                await repo.CommitDeltaAsync(delta, TestContext.Current.CancellationToken);
            }

            var snapshotBefore = repo.GetRepoView();
            var metaBefore = repo.Meta;

            var logDir = Path.Combine(_repoDir, "log");
            _ = Directory.Exists(logDir)
                ? Directory.GetFiles(logDir, "*.delta").OrderBy(p => p).ToArray()
                : [];

            // Act: dispose the repo (will flush meta, snapshots, and delete obsolete deltas)
            await repo.DisposeAsync();

            // Assert no stray .tmp files remain anywhere under repo path
            var tmpFiles = Directory.GetFiles(_repoDir, "*.tmp", SearchOption.AllDirectories);
            Assert.Empty(tmpFiles);

            // Reopen and compare
            var reopened = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

            var snapshotAfter = reopened.GetRepoView();
            var metaAfter = reopened.Meta;

            // Snapshot equality
            Assert.Equal(snapshotBefore.Dirs.Count, snapshotAfter.Dirs.Count);
            Assert.Equal(snapshotBefore.Files.Count, snapshotAfter.Files.Count);

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

            // Meta should be at least consistent on key fields
            Assert.Equal(metaBefore.Generation, metaAfter.Generation);
            Assert.Equal(metaBefore.NextLogSequence, metaAfter.NextLogSequence);
            Assert.Equal(metaBefore.LastSnapshottedLogSequence, metaAfter.LastSnapshottedLogSequence);
            Assert.Equal(metaBefore.SchemaVersion, metaAfter.SchemaVersion);
            Assert.Equal(metaBefore.RepoId, metaAfter.RepoId);

            // All remaining delta files, if any, must have seq > LastSnapshottedLogSequence
            var logFilesAfter = Directory.Exists(logDir)
                ? Directory.GetFiles(logDir, "*.delta").OrderBy(p => p).ToArray()
                : [];

            foreach (var path in logFilesAfter)
            {
                var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<seq>"
                var dash = name.IndexOf('-');
                Assert.NotEqual(-1, dash);

                var seqPart = name[(dash + 1)..];
                Assert.True(long.TryParse(seqPart, out var seq));
                Assert.True(seq > metaAfter.LastSnapshottedLogSequence);
            }

            await reopened.DisposeAsync();
        }

        [Fact]
        public async Task SaveScanSnapshots_ThenDisposeAsync_ThenReopen_PreservesState_ForScanRoot()
        {
            var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);

            var rootPath = Path.Combine(_repoDir, "root");
            Directory.CreateDirectory(rootPath);

            // 1. Do a small scan to populate dirs under a real ScanRoot
            await using (var session = (ScanSession)repo.BeginScan(
                             rootPath: rootPath,
                             scanOperation: default(ScanOperation),
                             volumeInfo: null,
                             maxFilesBeforeFlush: 16,
                             maxDirsBeforeFlush: 16))
            {
                // Root dir comes from BeginScan’s dummy record
                var rootDir = session.RootDir;

                // Mark root as enumerated
                var rootDirId = session.AddOrUpdateDirectory(
                    rootDir with { Status = ScanEntryStatus.Enumerated });

                // Add a few child dirs under this root
                for (var i = 0; i < 3; i++)
                {
                    var child = new DirRecord
                    {
                        DirId = 0, // let session allocate
                        ParentDirId = rootDirId,
                        Name = $"child-{i}",
                        Status = ScanEntryStatus.Enumerated
                    };

                    session.AddOrUpdateDirectory(child);
                }

                await session.CompleteAsync(TestContext.Current.CancellationToken);
            }

            var snapshotBefore = repo.GetRepoView();

            // 2. Force snapshot baseline
            repo.SaveScanSnapshots();
            var metaAfterSnapshot = repo.Meta;

            // 3. Dispose (will delete deltas up to LastSnapshottedLogSequence)
            await repo.DisposeAsync();

            // 4. Reopen and compare
            var reopened = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
            var snapshotReopened = reopened.GetRepoView();
            var metaReopened = reopened.Meta;

            // Snapshot equality
            Assert.Equal(snapshotBefore.Dirs.Count, snapshotReopened.Dirs.Count);
            Assert.Equal(snapshotBefore.Files.Count, snapshotReopened.Files.Count);
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

            // Meta baseline invariants
            Assert.Equal(metaAfterSnapshot.Generation, metaReopened.Generation);
            Assert.Equal(metaAfterSnapshot.LastSnapshottedLogSequence, metaReopened.LastSnapshottedLogSequence);
            Assert.Equal(metaAfterSnapshot.RepoId, metaReopened.RepoId);
            Assert.Equal(metaAfterSnapshot.SchemaVersion, metaReopened.SchemaVersion);

            await reopened.DisposeAsync();
        }
    }
}
