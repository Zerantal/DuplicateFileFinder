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
    public sealed class RepoConcurrencyTests : IDisposable
    {
        private readonly string _repoDir;

        public RepoConcurrencyTests()
        {
            _repoDir = Path.Combine(
                Path.GetTempPath(),
                "dff-repo-concurrency-tests",
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

        [Fact]
        public async Task CommitDeltaAsync_128Deltas_InParallel_ProducesConsistentSnapshotAndSequentialLogs()
        {
            const int deltaCount = 128;

            // Open a fresh repo
            var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;

            // Pre-allocate IDs and deltas single-threaded
            var deltas = new List<(RepoDelta Delta, long DirId, long FileId)>(deltaCount);

            for (var i = 0; i < deltaCount; i++)
            {
                var dirId = repo.AllocateDirId();
                var fileId = repo.AllocateFileId();

                var dir = new DirRecord
                {
                    DirId                = dirId,
                    ParentDirId          = null,
                    Name                 = $"dir-{i}",
                    LastSeenScanSequence = i,
                    Status               = ScanEntryStatus.Enumerated,
                    ErrorMessage         = null
                };

                var file = new FileRecord
                {
                    FileId               = fileId,
                    DirId                = dirId,
                    Name                 = $"file-{i}.dat",
                    Size                 = 100 + i,
                    Hash                 = default,
                    Created              = now,
                    Modified             = now,
                    LastSeenScanSequence = i,
                    Status               = ScanEntryStatus.Enumerated,
                    ErrorMessage         = null
                };

                var delta = new RepoDelta
                {
                    ScanSequence = i,
                    Dirs         = new[] { dir },
                    Files        = new[] { file }
                };

                deltas.Add((delta, dirId, fileId));
            }

            // Shuffle to avoid any accidental ordering assumptions
            Shuffle(deltas, new Random(123));

            // Run CommitDeltaAsync in parallel
            await Task.WhenAll(
                deltas.Select(d => repo.CommitDeltaAsync(d.Delta)));

            // --- Live snapshot assertions ---

            var snapshot = repo.GetRepoView();

            // All dirs present
            var expectedDirIds = deltas.Select(d => d.DirId).ToHashSet();
            Assert.True(expectedDirIds.SetEquals(snapshot.Dirs.Keys));

            // All files present
            var expectedFileIds = deltas.Select(d => d.FileId).ToHashSet();
            Assert.True(expectedFileIds.SetEquals(snapshot.Files.Keys));

            // Per-dir invariants (names and basic fields)
            foreach (var (delta, dirId, _) in deltas)
            {
                var dir = snapshot.Dirs[dirId];
                Assert.Equal($"dir-{delta.ScanSequence}", dir.Name);
                Assert.Equal(ScanEntryStatus.Enumerated, dir.Status);
            }

            // Per-file invariants
            foreach (var (delta, _, fileId) in deltas)
            {
                var file = snapshot.Files[fileId];
                Assert.Equal($"file-{delta.ScanSequence}.dat", file.Name);
                Assert.Equal(ScanEntryStatus.Enumerated, file.Status);
            }

            // ---- Assertions on the log files and meta ----

            var logDir = Path.Combine(_repoDir, "log");
            Assert.True(Directory.Exists(logDir));

            var logFiles = Directory.GetFiles(logDir, "*.delta");
            Assert.Equal(deltaCount, logFiles.Length);

            // Extract log sequence numbers from "<gen>-<seq>.delta"
            var logSeqs = new HashSet<long>();
            foreach (var path in logFiles)
            {
                var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<seq>"
                var dash = name.IndexOf('-');
                Assert.NotEqual(-1, dash);

                var seqPart = name[(dash + 1)..];
                Assert.True(long.TryParse(seqPart, out var seq));

                logSeqs.Add(seq);
            }

            // Sequences are unique and contiguous [0..deltaCount-1]
            Assert.Equal(deltaCount, logSeqs.Count);
            for (var i = 0; i < deltaCount; i++)
            {
                Assert.Contains(i, logSeqs);
            }

            // Meta.NextLogSequence should have advanced by deltaCount
            Assert.Equal(deltaCount, repo.Meta.NextLogSequence);

            // ---- Reopen the repo and ensure state is reconstructable ----

            await repo.DisposeAsync();

            var reopened = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
            var snapshot2 = reopened.GetRepoView();

            Assert.Equal(snapshot.Dirs.Count, snapshot2.Dirs.Count);
            Assert.Equal(snapshot.Files.Count, snapshot2.Files.Count);

            // Compare contents more strictly
            foreach (var kv in snapshot.Dirs)
            {
                Assert.True(snapshot2.Dirs.TryGetValue(kv.Key, out var d2));
                Assert.Equal(kv.Value.DirId, d2.DirId);
                Assert.Equal(kv.Value.Name, d2.Name);
                Assert.Equal(kv.Value.Status, d2.Status);
            }

            foreach (var kv in snapshot.Files)
            {
                Assert.True(snapshot2.Files.TryGetValue(kv.Key, out var f2));
                Assert.Equal(kv.Value.FileId, f2.FileId);
                Assert.Equal(kv.Value.DirId, f2.DirId);
                Assert.Equal(kv.Value.Name, f2.Name);
                Assert.Equal(kv.Value.Size, f2.Size);
                Assert.Equal(kv.Value.Status, f2.Status);
            }

            await reopened.DisposeAsync();
        }

        [Fact]
        public async Task CommitDeltaAsync_128Deltas_WithConcurrentSnapshots_RemainsConsistent()
        {
            const int deltaCount = 128;

            var repo = await Repo.OpenAsync(_repoDir, TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;

            var deltas = new List<(RepoDelta Delta, long DirId, long FileId)>(deltaCount);

            for (var i = 0; i < deltaCount; i++)
            {
                var dirId  = repo.AllocateDirId();
                var fileId = repo.AllocateFileId();

                var dir = new DirRecord
                {
                    DirId                = dirId,
                    ParentDirId          = null,
                    Name                 = $"dir-{i}",
                    LastSeenScanSequence = i,
                    Status               = ScanEntryStatus.Enumerated,
                    ErrorMessage         = null
                };

                var file = new FileRecord
                {
                    FileId               = fileId,
                    DirId                = dirId,
                    Name                 = $"file-{i}.dat",
                    Size                 = 100 + i,
                    Hash                 = default,
                    Created              = now,
                    Modified             = now,
                    LastSeenScanSequence = i,
                    Status               = ScanEntryStatus.Enumerated,
                    ErrorMessage         = null
                };

                var delta = new RepoDelta
                {
                    ScanSequence = i,
                    Dirs         = new[] { dir },
                    Files        = new[] { file }
                };

                deltas.Add((delta, dirId, fileId));
            }

            Shuffle(deltas, new Random(456));

            var cts = new CancellationTokenSource();
            var snapshotExceptions = new List<Exception>();

            // Snapshot hammer task: repeatedly call GetSnapshot while commits are running.
            var snapshotTask = Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var snap = repo.GetRepoView();

                        // Basic invariant: no duplicate keys and no null maps.
                        Assert.NotNull(snap.Dirs);
                        Assert.NotNull(snap.Files);
                        Assert.Equal(snap.Dirs.Count, snap.Dirs.Keys.Distinct().Count());
                        Assert.Equal(snap.Files.Count, snap.Files.Keys.Distinct().Count());

                        // Yield back to avoid hogging the thread.
                        await Task.Yield();
                    }
                }
                catch (Exception ex)
                {
                    lock (snapshotExceptions)
                    {
                        snapshotExceptions.Add(ex);
                    }
                }
            }, cts.Token);

            // Run all commits in parallel
            var commitTask = Task.WhenAll(deltas.Select(d => repo.CommitDeltaAsync(d.Delta)));

            await commitTask;

            // Stop snapshot hammer and wait for it
            cts.Cancel();
            await snapshotTask;

            // Ensure the snapshot loop never threw
            Assert.Empty(snapshotExceptions);

            // Final snapshot sanity check (as in previous test)
            var snapshot = repo.GetRepoView();

            var expectedDirIds = deltas.Select(d => d.DirId).ToHashSet();
            Assert.True(expectedDirIds.SetEquals(snapshot.Dirs.Keys));

            var expectedFileIds = deltas.Select(d => d.FileId).ToHashSet();
            Assert.True(expectedFileIds.SetEquals(snapshot.Files.Keys));

            var logDir = Path.Combine(_repoDir, "log");
            var logFiles = Directory.GetFiles(logDir, "*.delta");
            Assert.Equal(deltaCount, logFiles.Length);

            var logSeqs = new HashSet<long>();
            foreach (var path in logFiles)
            {
                var name = Path.GetFileNameWithoutExtension(path); // "<gen>-<seq>"
                var dash = name.IndexOf('-');
                Assert.NotEqual(-1, dash);

                var seqPart = name[(dash + 1)..];
                Assert.True(long.TryParse(seqPart, out var seq));
                logSeqs.Add(seq);
            }

            Assert.Equal(deltaCount, logSeqs.Count);
            for (var i = 0; i < deltaCount; i++)
                Assert.Contains(i, logSeqs);

            Assert.Equal(deltaCount, repo.Meta.NextLogSequence);

            await repo.DisposeAsync();
        }

        private static void Shuffle<T>(IList<T> list, Random rng)
        {
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
