using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository
{
    public sealed class HashIndexPluginTests
    {
        private static HashKey NewHash(int seed)
        {
            var bytes = new byte[16];
            new Random(seed).NextBytes(bytes);
            return new HashKey(bytes);
        }

        private static RepoViewSnapshot MakeSnapshot(params FileRecord[] files)
        {
            // Dirs/HashIndex are unused by HashIndexPlugin today; provide minimal stubs.
            var dirs = new Dictionary<long, DirRecord>();
            var fileDict = files.ToDictionary(f => f.FileId, f => f);

            return new RepoViewSnapshot
            {
                Dirs      = dirs,
                Files     = fileDict,
            };
        }

        private static RepoEvent MakeBootstrapEvent(RepoViewSnapshot snapshot)
        {
            return new BootstrapEvent
            {
                Generation      = 1,
                NextLogSequence = 1,
                Snapshot        = snapshot
            };
        }

        private static RepoEvent MakeDeltaCommittedEvent(long generation, long nextLogSeq, long scanSeq, RepoDelta delta)
        {
            return new DeltaCommittedEvent
            {
                Generation      = generation,
                NextLogSequence = nextLogSeq,
                ScanSequence    = scanSeq,
                Delta           = delta
            };
        }

        private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
        {
            var start = DateTime.UtcNow;
            while (!condition())
            {
                if (DateTime.UtcNow - start > timeout)
                    throw new TimeoutException("Condition was not satisfied in time.");

                await Task.Delay(10);
            }
        }

        [Fact]
        public async Task OpenedEvent_BuildsInitialDuplicateGroups()
        {
            var hashDup = NewHash(1);
            var hashUnique = NewHash(2);

            var f1 = new FileRecord
            {
                FileId              = 1,
                DirId               = 10,
                Name                = "a.bin",
                Size                = 100,
                Hash                = hashDup,
                Modified            = DateTimeOffset.UtcNow,
                Created             = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status              = ScanEntryStatus.Enumerated,
                ErrorMessage        = null
            };

            var f2 = new FileRecord
            {
                FileId              = 2,
                DirId               = 11,
                Name                = "b.bin",
                Size                = 100,
                Hash                = hashDup,
                Modified            = DateTimeOffset.UtcNow,
                Created             = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status              = ScanEntryStatus.Enumerated,
                ErrorMessage        = null
            };

            var f3 = new FileRecord
            {
                FileId              = 3,
                DirId               = 12,
                Name                = "c.bin",
                Size                = 100,
                Hash                = hashUnique,
                Modified            = DateTimeOffset.UtcNow,
                Created             = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status              = ScanEntryStatus.Enumerated,
                ErrorMessage        = null
            };

            var snapshot = MakeSnapshot(f1, f2, f3);

            await using var plugin = new HashIndexPlugin();

            // Act: simulate repo open
            plugin.Post(MakeBootstrapEvent(snapshot));

            // Wait until the plugin has processed the event and built the index
            await WaitForConditionAsync(
                () => plugin.GetDuplicateGroups().Count > 0,
                TimeSpan.FromSeconds(2));

            var groups = plugin.GetDuplicateGroups();

            // We expect exactly one group (hashDup) with f1 + f2
            var group = Assert.Single(groups);
            Assert.Equal(2, group.Count);
            Assert.Contains(1L, group.Select(g => g.FileId));
            Assert.Contains(2L, group.Select(g => g.FileId));
        }

        [Fact]
        public async Task DeltaCommitted_AddsNewDuplicateFile_FormsGroup()
        {
            var hashDup = NewHash(10);

            // Start with a single file (no duplicates yet)
            var f1 = new FileRecord
            {
                FileId              = 1,
                DirId               = 10,
                Name                = "a.bin",
                Size                = 100,
                Hash                = hashDup,
                Modified            = DateTimeOffset.UtcNow,
                Created             = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status              = ScanEntryStatus.Enumerated,
                ErrorMessage        = null
            };

            var snapshot = MakeSnapshot(f1);

            await using var plugin = new HashIndexPlugin();

            plugin.Post(MakeBootstrapEvent(snapshot));

            // Wait until initial state is processed (no duplicate groups expected)
            await WaitForConditionAsync(
                () => plugin.GetDuplicateGroups().Count == 0,
                TimeSpan.FromSeconds(2));

            // Now commit a delta that adds a second file with the same hash
            var f2 = new FileRecord
            {
                FileId              = 2,
                DirId               = 11,
                Name                = "b.bin",
                Size                = 100,
                Hash                = hashDup,
                Modified            = DateTimeOffset.UtcNow,
                Created             = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 2,
                Status              = ScanEntryStatus.Enumerated,
                ErrorMessage        = null
            };

            var delta = new RepoDelta
            {
                ScanSequence = 2,
                Dirs         = Array.Empty<DirRecord>(),
                Files        = new[] { f2 }
            };

            plugin.Post(MakeDeltaCommittedEvent(generation: 1, nextLogSeq: 2, scanSeq: 2, delta));

            await WaitForConditionAsync(
                () => plugin.GetDuplicateGroups().Count == 1,
                TimeSpan.FromSeconds(2));

            var groups = plugin.GetDuplicateGroups();
            var group  = Assert.Single(groups);

            Assert.Equal(2, group.Count);
            Assert.Contains(1L, group.Select(g => g.FileId));
            Assert.Contains(2L, group.Select(g => g.FileId));
        }

        [Fact]
        public async Task DeltaCommitted_RemovedFile_RemovesFromDuplicateGroup()
        {
            var hashDup = NewHash(20);

            // Start with two files in the same hash group
            var f1 = new FileRecord
            {
                FileId              = 1,
                DirId               = 10,
                Name                = "a.bin",
                Size                = 100,
                Hash                = hashDup,
                Modified            = DateTimeOffset.UtcNow,
                Created             = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status              = ScanEntryStatus.Enumerated,
                ErrorMessage        = null
            };

            var f2 = new FileRecord
            {
                FileId              = 2,
                DirId               = 11,
                Name                = "b.bin",
                Size                = 100,
                Hash                = hashDup,
                Modified            = DateTimeOffset.UtcNow,
                Created             = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status              = ScanEntryStatus.Enumerated,
                ErrorMessage        = null
            };

            var snapshot = MakeSnapshot(f1, f2);

            await using var plugin = new HashIndexPlugin();

            plugin.Post(MakeBootstrapEvent(snapshot));

            await WaitForConditionAsync(
                () => plugin.GetDuplicateGroups().Count == 1,
                TimeSpan.FromSeconds(2));

            // Now commit a delta that marks f2 as removed
            var removedF2 = f2 with { Status = ScanEntryStatus.Deleted };

            var delta = new RepoDelta
            {
                ScanSequence = 2,
                Dirs         = [],
                Files        = [removedF2]
            };

            plugin.Post(MakeDeltaCommittedEvent(generation: 1, nextLogSeq: 3, scanSeq: 2, delta));

            // After removing one file, the hash should no longer have a duplicate group (only one left).
            await WaitForConditionAsync(
                () => plugin.GetDuplicateGroups().Count == 0,
                TimeSpan.FromSeconds(2));

            var groupsAfter = plugin.GetDuplicateGroups();
            Assert.Empty(groupsAfter);
        }

        [Fact]
        public async Task DefaultHash_IsIgnored_AndDoesNotProduceGroup()
        {
            var defaultHash = default(HashKey);

            var f1 = new FileRecord
            {
                FileId              = 1,
                DirId               = 10,
                Name                = "a.bin",
                Size                = 100,
                Hash                = defaultHash,
                Modified            = DateTimeOffset.UtcNow,
                Created             = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status              = ScanEntryStatus.Enumerated,
                ErrorMessage        = null
            };

            var f2 = new FileRecord
            {
                FileId              = 2,
                DirId               = 11,
                Name                = "b.bin",
                Size                = 100,
                Hash                = defaultHash,
                Modified            = DateTimeOffset.UtcNow,
                Created             = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status              = ScanEntryStatus.Enumerated,
                ErrorMessage        = null
            };

            var snapshot = MakeSnapshot(f1, f2);

            await using var plugin = new HashIndexPlugin();

            plugin.Post(MakeBootstrapEvent(snapshot));

            // Even though there are two files with the same default hash,
            // the plugin should ignore default hashes and not produce a group.
            await WaitForConditionAsync(
                () => plugin.GetDuplicateGroups().Count == 0,
                TimeSpan.FromSeconds(2));

            Assert.Empty(plugin.GetDuplicateGroups());
        }
    }
}
