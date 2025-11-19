using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository
{
    public sealed class ScanSessionTests : IDisposable
    {
        private readonly string _rootDir;

        public ScanSessionTests()
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "dff-scan-session-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_rootDir))
                    Directory.Delete(_rootDir, recursive: true);
            }
            catch
            {
                // ignore cleanup errors in tests
            }
        }

        private string SnapshotPath => Path.Combine(_rootDir, "snapshot.bin");

        private RepoSnapshot ReadSnapshot()
        {
            var bytes = File.ReadAllBytes(SnapshotPath);
            return MemoryPackSerializer.Deserialize<RepoSnapshot>(bytes)!;
        }

        // ReSharper disable once InconsistentNaming
        private static string RootPathForCurrentOS() =>
            OperatingSystem.IsWindows() ? "root" : "/root";

        private static async Task<RepoSnapshot?> WaitForSnapshotAsync(
            Repo repo,
            string snapshotPath,
            Func<RepoSnapshot, bool> predicate,
            TimeSpan timeout)
        {
            var start = DateTime.UtcNow;

            while (true)
            {
                repo.SaveSnapshot();

                if (File.Exists(snapshotPath))
                {
                    var bytes    = await File.ReadAllBytesAsync(snapshotPath);
                    var snapshot = MemoryPackSerializer.Deserialize<RepoSnapshot>(bytes);
                    if (snapshot != null && predicate(snapshot))
                        return snapshot;
                }

                if (DateTime.UtcNow - start > timeout)
                    return null;

                await Task.Delay(10);
            }
        }
        
        // --------------------------------------------------------------------
        //    Progressive flush: Observe* + FlushProgress should commit a delta
        //    and update repo state, but NOT mark the run completed.
        // --------------------------------------------------------------------
        [Fact]
        public async Task FlushProgress_CommitsObservedDirsAndFiles_WithoutCompletingRun()
        {
            var repo = Repo.Open(_rootDir);
            var rootPath = RootPathForCurrentOS();

            var session = repo.BeginScan(rootPath);

            var dirId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            session.ObserveDir(
                id: dirId,
                parentId: null,
                name: "root",
                status: ScanEntryStatus.Enumerated);

            var hashBytes = new byte[16];
            new Random(123).NextBytes(hashBytes);
            var hashKey = new HashKey(hashBytes);

            session.ObserveFile(
                id: fileId,
                dirId: dirId,
                name: "file.txt",
                size: 100,
                hash: hashKey,
                modified: DateTimeOffset.UtcNow,
                created: DateTimeOffset.UtcNow,
                status: ScanEntryStatus.Enumerated | ScanEntryStatus.Hashed);

            await session.FlushProgressAsync();

            // Do not call CompleteAsync / FailAsync here.

            repo.SaveSnapshot();
            var snapshot = ReadSnapshot();

            // Files and dirs should be present
            Assert.Single(snapshot.Dirs);
            Assert.Single(snapshot.Files);

            var dir = Assert.Single(snapshot.Dirs).Value;
            Assert.Equal(dirId, dir.Id);
            Assert.Equal("root", dir.Name);
            Assert.Equal(session.ScanSequence, dir.LastSeenSequence);

            var file = Assert.Single(snapshot.Files).Value;
            Assert.Equal(fileId, file.Id);
            Assert.Equal("file.txt", file.Name);
            Assert.Equal(session.ScanSequence, file.LastSeenScanSequence);

            // ScanRun should exist and still be InProgress
            var run = Assert.Single(snapshot.ScanRuns);
            Assert.Equal(session.ScanSequence, run.ScanSequence);
            Assert.Equal(ScanRunStatus.InProgress, run.Status);

            await session.DisposeAsync();
        }

        // --------------------------------------------------------------------
        //    CompleteAsync: progressive scan + completion should emit
        //    tombstones for entries under the root that were not seen
        //    in this scan sequence.
        // --------------------------------------------------------------------
        [Fact]
        public async Task CompleteAsync_EmitsTombstonesForUnseenEntriesUnderRoot()
        {
            var repo = Repo.Open(_rootDir);
            var rootPath = RootPathForCurrentOS();

            // Seed repo with an "old" file under /root/sub seen at scan sequence 1
            var rootDirId = Guid.NewGuid();
            var subDirId = Guid.NewGuid();
            var oldFileId = Guid.NewGuid();

            var hashBytes = new byte[16];
            new Random(111).NextBytes(hashBytes);
            var hash = new HashKey(hashBytes);
            var seq = repo.AllocateScanSequence();

            var rootDir = new DirRecord
            {
                Id = rootDirId,
                ParentId = null,
                Name = "root",
                LastSeenSequence = seq,
                Status = ScanEntryStatus.Enumerated
            };

            var subDir = new DirRecord
            {
                Id = subDirId,
                ParentId = rootDirId,
                Name = "sub",
                LastSeenSequence = seq,
                Status = ScanEntryStatus.Enumerated
            };

            var oldFile = new FileRecord
            {
                Id = oldFileId,
                DirId = subDirId,
                Name = "old.txt",
                Size = 1,
                Hash = hash,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = seq,
                Status = ScanEntryStatus.Enumerated
            };

            var scanSeq = repo.AllocateLogId();
            await repo.CommitDeltaAsync(new RepoDelta
            {
                ScanSequence = scanSeq,
                Dirs = [rootDir, subDir],
                Files = [oldFile]
            });

            repo.SaveSnapshot();
            var snapshot1 = ReadSnapshot();
            Assert.Single(snapshot1.Files);
            Assert.True(snapshot1.Files.ContainsKey(oldFileId));

            // New scan: only see a new file under the same root.
            var session = repo.BeginScan(rootPath);
            var newFileId = Guid.NewGuid();

            session.ObserveDir(
                id: rootDirId,
                parentId: null,
                name: "root",
                status: ScanEntryStatus.Enumerated);

            session.ObserveDir(
                id: subDirId,
                parentId: rootDirId,
                name: "sub",
                status: ScanEntryStatus.Enumerated);

            session.ObserveFile(
                id: newFileId,
                dirId: subDirId,
                name: "new.txt",
                size: 2,
                hash: hash,
                modified: DateTimeOffset.UtcNow,
                created: DateTimeOffset.UtcNow,
                status: ScanEntryStatus.Enumerated | ScanEntryStatus.Hashed);

            // Progressive flush + completion
            await session.FlushProgressAsync();
            await session.CompleteAsync();

            repo.SaveSnapshot();
            var snapshot2 = ReadSnapshot();

            // Old file should have been tombstoned and removed; new file remains.
            Assert.Single(snapshot2.Files);
            Assert.True(snapshot2.Files.ContainsKey(newFileId));
            Assert.False(snapshot2.Files.ContainsKey(oldFileId));

            // ScanRun should be marked Completed for this sequence.
            var run = Assert.Single(snapshot2.ScanRuns, r => r.ScanSequence == session.ScanSequence);
            Assert.Equal(ScanRunStatus.Completed, run.Status);
        }

        // --------------------------------------------------------------------
        //    FailAsync: marking a scan as failed/cancelled should not
        //    generate tombstones, even if there was prior content.
        // --------------------------------------------------------------------
        [Fact]
        public async Task FailAsync_DoesNotEmitTombstones()
        {
            var repo = Repo.Open(_rootDir);
            var rootPath = RootPathForCurrentOS();

            // Seed with an existing file under root
            var dirId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            var hashBytes = new byte[16];
            new Random(222).NextBytes(hashBytes);
            var hash = new HashKey(hashBytes);

            var dir = new DirRecord
            {
                Id = dirId,
                ParentId = null,
                Name = "root",
                LastSeenSequence = 1,
                Status = ScanEntryStatus.Enumerated
            };

            var file = new FileRecord
            {
                Id = fileId,
                DirId = dirId,
                Name = "keep.txt",
                Size = 1,
                Hash = hash,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Enumerated
            };

            await repo.CommitDeltaAsync(new RepoDelta
            {
                ScanSequence = 1,
                Dirs = [dir],
                Files = [file]
            });

            repo.SaveSnapshot();
            var snapshot1 = ReadSnapshot();
            Assert.Single(snapshot1.Files);
            Assert.True(snapshot1.Files.ContainsKey(fileId));

            // Start a new scan but fail it.
            var session = repo.BeginScan(rootPath);

            // Optionally observe some stuff, but never complete.
            session.ObserveDir(dirId, null, "root", ScanEntryStatus.Enumerated);
            await session.FlushProgressAsync();

            await session.FailAsync("cancelled", cancelled: true);

            repo.SaveSnapshot();
            var snapshot2 = ReadSnapshot();

            // Original file must still be present; no tombstone-based deletion.
            Assert.True(snapshot2.Files.ContainsKey(fileId));

            // ScanRun for this sequence should be Failed or Cancelled.
            var run = Assert.Single(snapshot2.ScanRuns, r => r.ScanSequence == session.ScanSequence);
            Assert.True(run.Status == ScanRunStatus.Failed || run.Status == ScanRunStatus.Cancelled);
            Assert.Equal("cancelled", run.ErrorMessage);
        }

        // --------------------------------------------------------------------
        //    DisposeAsync: if a ScanSession is disposed without CompleteAsync
        //    or FailAsync, it should mark the run as failed/cancelled and
        //    not emit deletions.
        // --------------------------------------------------------------------
        [Fact]
        public async Task DisposeAsync_WithoutCompletion_MarksRunFailedWithoutDeletions()
        {
            var repo = Repo.Open(_rootDir);
            var rootPath = RootPathForCurrentOS();

            // Seed with an existing file under root
            var dirId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            var hashBytes = new byte[16];
            new Random(333).NextBytes(hashBytes);
            var hash = new HashKey(hashBytes);

            var dir = new DirRecord
            {
                Id = dirId,
                ParentId = null,
                Name = "root",
                LastSeenSequence = 1,
                Status = ScanEntryStatus.Enumerated
            };

            var file = new FileRecord
            {
                Id = fileId,
                DirId = dirId,
                Name = "keep.txt",
                Size = 1,
                Hash = hash,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Enumerated
            };

            await repo.CommitDeltaAsync(new RepoDelta
            {
                ScanSequence = 1,
                Dirs = [dir],
                Files = [file]
            });

            repo.SaveSnapshot();
            var snapshot1 = ReadSnapshot();
            Assert.True(snapshot1.Files.ContainsKey(fileId));

            // Start a new scan, observe something, but neither complete nor fail explicitly.
            var session = repo.BeginScan(rootPath);

            session.ObserveDir(dirId, null, "root", ScanEntryStatus.Enumerated);
            await session.FlushProgressAsync();

            await session.DisposeAsync(); // should mark run failed/cancelled

            repo.SaveSnapshot();
            var snapshot2 = ReadSnapshot();

            // Original file must still be present.
            Assert.True(snapshot2.Files.ContainsKey(fileId));

            // ScanRun for this sequence should not be InProgress anymore.
            var run = Assert.Single(snapshot2.ScanRuns, r => r.ScanSequence == session.ScanSequence);
            Assert.NotEqual(ScanRunStatus.InProgress, run.Status);
        }

        // --------------------------------------------------------------------
        //    If thresholds are not reached, nothing is flushed automatically:
        //    - Snapshot before FlushProgress() should contain nothing.
        //    - Snapshot after FlushProgress() should contain the buffered entries.
        // --------------------------------------------------------------------
        [Fact]
        public async Task NoAutoFlush_BelowThreshold_RequiresExplicitFlush()
        {
            var repo = Repo.Open(_rootDir);
            var rootPath = RootPathForCurrentOS();

            // High thresholds: no auto-flush
            await using var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 10, maxDirsBeforeFlush: 10);

            var dirId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            var hashBytes = new byte[16];
            new Random(456).NextBytes(hashBytes);
            var hashKey = new HashKey(hashBytes);

            session.ObserveDir(
                id: dirId,
                parentId: null,
                name: "root",
                status: ScanEntryStatus.Enumerated);

            session.ObserveFile(
                id: fileId,
                dirId: dirId,
                name: "f1.txt",
                size: 10,
                hash: hashKey,
                modified: DateTimeOffset.UtcNow,
                created: DateTimeOffset.UtcNow,
                status: ScanEntryStatus.Enumerated | ScanEntryStatus.Hashed);

            // Below threshold: nothing should have been flushed yet.
            repo.SaveSnapshot();
            var snapshotBefore = ReadSnapshot();

            Assert.Empty(snapshotBefore.Dirs);
            Assert.Empty(snapshotBefore.Files);

            // Now explicitly flush and snapshot again
            await session.FlushProgressAsync();
            repo.SaveSnapshot();
            var snapshotAfter = ReadSnapshot();

            Assert.Single(snapshotAfter.Dirs);
            Assert.Single(snapshotAfter.Files);

            Assert.True(snapshotAfter.Dirs.ContainsKey(dirId));
            Assert.True(snapshotAfter.Files.ContainsKey(fileId));
        }
        
        // --------------------------------------------------------------------
        //    Auto-flush when file threshold is reached (async).
        //    We rely on the background FlushProgressAsync() triggered by ObserveFile.
        // --------------------------------------------------------------------
        [Fact]
        public async Task AutoFlush_WhenFileThresholdReached_CommitsDelta_Async()
        {
            var repo     = Repo.Open(_rootDir);
            var rootPath = RootPathForCurrentOS();

            var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 2, maxDirsBeforeFlush: 1000);

            var dirId   = Guid.NewGuid();
            var fileId1 = Guid.NewGuid();
            var fileId2 = Guid.NewGuid();

            session.ObserveDir(
                id: dirId,
                parentId: null,
                name: "root",
                status: ScanEntryStatus.Enumerated);

            var hashBytes = new byte[16];
            new Random(123).NextBytes(hashBytes);
            var hashKey = new HashKey(hashBytes);

            // First file (buffered)
            session.ObserveFile(
                id: fileId1,
                dirId: dirId,
                name: "f1.txt",
                size: 10,
                hash: hashKey,
                modified: DateTimeOffset.UtcNow,
                created: DateTimeOffset.UtcNow,
                status: ScanEntryStatus.Enumerated | ScanEntryStatus.Hashed);

            // Second file: exceeds threshold, should trigger auto FlushProgressAsync internally
            session.ObserveFile(
                id: fileId2,
                dirId: dirId,
                name: "f2.txt",
                size: 20,
                hash: hashKey,
                modified: DateTimeOffset.UtcNow,
                created: DateTimeOffset.UtcNow,
                status: ScanEntryStatus.Enumerated | ScanEntryStatus.Hashed);

            // Wait until snapshot sees both files (or timeout)
            var snapshot = await WaitForSnapshotAsync(
                repo,
                SnapshotPath,
                s => s.Files.Count == 2 && s.Dirs.Count == 1,
                timeout: TimeSpan.FromSeconds(2));

            Assert.NotNull(snapshot);
            Assert.Single(snapshot.Dirs);
            Assert.Equal(2, snapshot.Files.Count);
            Assert.True(snapshot.Dirs.ContainsKey(dirId));
            Assert.True(snapshot.Files.ContainsKey(fileId1));
            Assert.True(snapshot.Files.ContainsKey(fileId2));

            await session.DisposeAsync();
        }

        // --------------------------------------------------------------------
        // 2. Auto-flush when directory threshold is reached (async).
        // --------------------------------------------------------------------
        [Fact]
        public async Task AutoFlush_WhenDirThresholdReached_CommitsDelta_Async()
        {
            var repo     = Repo.Open(_rootDir);
            var rootPath = RootPathForCurrentOS();

            var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 1000, maxDirsBeforeFlush: 2);

            var dirId1 = Guid.NewGuid();
            var dirId2 = Guid.NewGuid();

            session.ObserveDir(
                id: dirId1,
                parentId: null,
                name: "root",
                status: ScanEntryStatus.Enumerated);

            // Second dir: exceeds threshold, should trigger auto flush
            session.ObserveDir(
                id: dirId2,
                parentId: dirId1,
                name: "sub",
                status: ScanEntryStatus.Enumerated);

            var snapshot = await WaitForSnapshotAsync(
                repo,
                SnapshotPath,
                s => s.Dirs.Count == 2,
                timeout: TimeSpan.FromSeconds(2));

            Assert.NotNull(snapshot);
            Assert.Equal(2, snapshot.Dirs.Count);
            Assert.True(snapshot.Dirs.ContainsKey(dirId1));
            Assert.True(snapshot.Dirs.ContainsKey(dirId2));

            await session.DisposeAsync();
        }

        // --------------------------------------------------------------------
        //    Below threshold there is no auto-flush:
        //    - Snapshot should be empty until FlushProgressAsync is awaited.
        // --------------------------------------------------------------------
        [Fact]
        public async Task BelowThreshold_NoAutoFlush_RequiresExplicitFlushAsync()
        {
            var repo     = Repo.Open(_rootDir);
            var rootPath = RootPathForCurrentOS();

            var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 10, maxDirsBeforeFlush: 10);

            var dirId  = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            var hashBytes = new byte[16];
            new Random(456).NextBytes(hashBytes);
            var hashKey = new HashKey(hashBytes);

            session.ObserveDir(
                id: dirId,
                parentId: null,
                name: "root",
                status: ScanEntryStatus.Enumerated);

            session.ObserveFile(
                id: fileId,
                dirId: dirId,
                name: "f1.txt",
                size: 10,
                hash: hashKey,
                modified: DateTimeOffset.UtcNow,
                created: DateTimeOffset.UtcNow,
                status: ScanEntryStatus.Enumerated | ScanEntryStatus.Hashed);

            // Thresholds not reached -> no auto flush expected
            repo.SaveSnapshot();
            if (File.Exists(SnapshotPath))
            {
                var snapshotBefore = ReadSnapshot();
                Assert.Empty(snapshotBefore.Dirs);
                Assert.Empty(snapshotBefore.Files);
            }

            // Explicit async flush
            await session.FlushProgressAsync();

            repo.SaveSnapshot();
            var snapshotAfter = ReadSnapshot();

            Assert.Single(snapshotAfter.Dirs);
            Assert.Single(snapshotAfter.Files);
            Assert.True(snapshotAfter.Dirs.ContainsKey(dirId));
            Assert.True(snapshotAfter.Files.ContainsKey(fileId));

            await session.DisposeAsync();
        }

        // --------------------------------------------------------------------
        //    Multiple auto-flushes: threshold hit several times during the scan.
        //    After a final FlushProgressAsync(), all files must be persisted.
        // --------------------------------------------------------------------
        [Fact]
        public async Task AutoFlush_CanTriggerMultipleTimes_AllDataPersisted_Async()
        {
            var repo     = Repo.Open(_rootDir);
            var rootPath = RootPathForCurrentOS();

            var session = repo.BeginScan(rootPath, maxFilesBeforeFlush: 2, maxDirsBeforeFlush: 1000);

            var dirId = Guid.NewGuid();
            session.ObserveDir(
                id: dirId,
                parentId: null,
                name: "root",
                status: ScanEntryStatus.Enumerated);

            var hashBytes = new byte[16];
            new Random(789).NextBytes(hashBytes);
            var hashKey = new HashKey(hashBytes);

            var ids = new List<Guid>();
            for (var i = 0; i < 5; i++)
            {
                var fileId = Guid.NewGuid();
                ids.Add(fileId);

                session.ObserveFile(
                    id: fileId,
                    dirId: dirId,
                    name: $"f{i}.txt",
                    size: 10 + i,
                    hash: hashKey,
                    modified: DateTimeOffset.UtcNow,
                    created: DateTimeOffset.UtcNow,
                    status: ScanEntryStatus.Enumerated | ScanEntryStatus.Hashed);
                // Auto-flush should fire at i=1,3, and then we explicitly flush at the end.
            }

            // Final explicit drain of any remaining buffered files and in-flight auto flushes
            await session.FlushProgressAsync();

            repo.SaveSnapshot();
            var snapshot = ReadSnapshot();

            Assert.Single(snapshot.Dirs);
            Assert.Equal(5, snapshot.Files.Count);

            foreach (var id in ids)
                Assert.True(snapshot.Files.ContainsKey(id), $"Expected file {id} to be present in snapshot.");

            await session.DisposeAsync();
        }
    }
}