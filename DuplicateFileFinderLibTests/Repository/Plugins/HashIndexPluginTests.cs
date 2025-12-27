using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Plugins;

public sealed class HashIndexPluginTests
{
    private readonly TempFsFixture _fs = new("hash-index");

    private static HashKey NewHash(int seed)
    {
        var bytes = new byte[16];
        new Random(seed).NextBytes(bytes);
        return new HashKey(bytes);
    }

    private static RepoEvent MakeBootstrapEvent(RepoSnapshotView snapshot)
    {
        return new BootstrapEvent
        {
            Generation = 1,
            RepoSnapshotView = snapshot
        };
    }

    private static RepoEvent MakeSnapshotCommittedEvent(long gen, long scanRootId, RepoSnapshotView snapshot)
    {
        return new ScanRootSnapshotCommittedEvent
        {
            Generation = gen,
            ScanRootId = scanRootId,
            RepoSnapshotView = snapshot
        };
    }

    [Fact]
    public async Task OpenedEvent_BuildsInitialDuplicateGroups()
    {
        var hashDup = NewHash(1);
        var hashUnique = NewHash(2);

        var snapshot = RepoUtil.MakeSnapshotV2(
            1,
            dirs: [],
            files:
            [
                ("a.bin", dirId: 10L, fileId: 1L, size: 100L, hash: hashDup),
                ("b.bin", dirId: 11L, fileId: 2L, size: 100L, hash: hashDup),
                ("c.bin", dirId: 12L, fileId: 3L, size: 100L, hash: hashUnique)
            ]);
        

        await using var plugin = new HashIndexPlugin(_fs.Root);

        // Act: simulate repo open
        plugin.Post(MakeBootstrapEvent(snapshot));

        // Wait until the plugin has processed the event and built the index
        await AsyncUtil.WaitForConditionAsync(
            () => plugin.GetDuplicateGroups().Count > 0,
            TimeSpan.FromSeconds(2));

        var groups = plugin.GetDuplicateGroups();

        var fileA = new FileHandle(1, 0);
        var fileB = new FileHandle(1, 1);

        // We expect exactly one group (hashDup) with f1 + f2
        var group = Assert.Single(groups);
        Assert.Equal(2, group.list.Count);
        Assert.Equal(RepoUtil.Sort([fileA, fileB]), RepoUtil.Sort(group.list.ToArray()));
    }

    [Fact]
    public async Task DefaultHash_IsIgnored_AndDoesNotProduceGroup()
    {
        var defaultHash = default(HashKey);
        
        var snapshot = RepoUtil.MakeSnapshotV2(
            1,
            dirs: [],
            files:
            [
                ("a.bin", dirId: 10L, fileId: 1L, size: 100L, hash: defaultHash),
                ("b.bin", dirId: 11L, fileId: 2L, size: 100L, hash: defaultHash)
            ]);
        

        await using var plugin = new HashIndexPlugin(_fs.Root);

        plugin.Post(MakeBootstrapEvent(snapshot));

        // Even though there are two files with the same default hash,
        // the plugin should ignore default hashes and not produce a group.
        await AsyncUtil. WaitForConditionAsync(
            () => plugin.GetDuplicateGroups().Count == 0,
            TimeSpan.FromSeconds(2));

        Assert.Empty(plugin.GetDuplicateGroups());
    }
    
    [Fact]
    public async Task RebuildFromSnapshot_DoesNotDoubleCountDuplicates_AcrossMultipleRoots()
    {
        var dir = Path.Combine(Path.GetTempPath(), "HashIndexTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);

        try
        {
            await using var plugin = new HashIndexPlugin(dir);

            // Two roots, both contain the same duplicate group:
            // hash H with size 100 appears twice in each root (2 files per root).
            // Correct total duplicates:
            //  - per root: (2-1)=1 duplicate file, 1*100 bytes
            //  - across 2 roots: 2 duplicates, 200 bytes
            var h = new HashKey([1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16]);

            var snapshot = new RepoSnapshotView
            {
                Snapshots =
                    new Dictionary<long, ScanRootSnapshotView>
                    {
                        [1] = MakeRoot(scanRootId: 1, dirId: 10, hash: h, size: 100),
                        [2] = MakeRoot(scanRootId: 2, dirId: 20, hash: h, size: 100)
                    },
                ScanRoots = null!
            };

            plugin.Post(new BootstrapEvent
            {
                Generation = 1,
                RepoSnapshotView = snapshot
            });

            await plugin.WhenReadyAsync(CancellationToken.None);

            Assert.Equal(3, plugin.TotalDuplicateFileCount);
            Assert.Equal(300, plugin.TotalSpaceTakenByDuplicates);

            var groups = plugin.GetDuplicateGroups(minDuplicates: 2, minSize: 1);
            Assert.Single(groups);
            Assert.Equal(100, groups[0].size);
            Assert.Equal(4, groups[0].list.Count); // 2 files per root => 4 total handles
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ScanRootSnapshotCommittedEvent_RebuildsIndex_WhenGenerationIncreases()
    {
        var dir = Path.Combine(Path.GetTempPath(), "HashIndexTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);

        try
        {
            await using var plugin = new HashIndexPlugin(dir);

            // Initial snapshot: one duplicate group of size 100 (2 files => 1 duplicate file)
            var h1 = NewHash(1);
            var snap1 = new RepoSnapshotView
            {
                Snapshots = new Dictionary<long, ScanRootSnapshotView>
                {
                    [1] = MakeRoot(scanRootId: 1, dirId: 10, hash: h1, size: 100)
                },
                ScanRoots = null!
            };

            plugin.Post(new BootstrapEvent { Generation = 1, RepoSnapshotView = snap1 });
            await plugin.WhenReadyAsync(CancellationToken.None);

            Assert.Equal(1, plugin.TotalDuplicateFileCount);
            Assert.Equal(100, plugin.TotalSpaceTakenByDuplicates);

            // Committed snapshot: new duplicate group of size 777 (3 files => 2 duplicate files)
            var h2 = NewHash(2);
            var snap2 = new RepoSnapshotView
            {
                Snapshots = new Dictionary<long, ScanRootSnapshotView>
                {
                    [1] = MakeRoot3Files(scanRootId: 1, dirId: 10, hash: h2, size: 777)
                },
                ScanRoots = null!
            };

            plugin.Post(MakeSnapshotCommittedEvent(gen: 2, scanRootId: 1, snapshot: snap2));

            await AsyncUtil.WaitForConditionAsync(
                () => plugin is { TotalDuplicateFileCount: 2, TotalSpaceTakenByDuplicates: 2 * 777 },
                TimeSpan.FromSeconds(2));

            Assert.Equal(2, plugin.TotalDuplicateFileCount);
            Assert.Equal(2 * 777, plugin.TotalSpaceTakenByDuplicates);

            var groups = plugin.GetDuplicateGroups(minDuplicates: 2, minSize: 1);
            var group = Assert.Single(groups);
            Assert.Equal(777, group.size);
            Assert.Equal(3, group.list.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ScanRootSnapshotCommittedEvent_IsIgnored_WhenGenerationDoesNotIncrease()
    {
        var dir = Path.Combine(Path.GetTempPath(), "HashIndexTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);

        try
        {
            await using var plugin = new HashIndexPlugin(dir);

            var h1 = NewHash(1);
            var snap1 = new RepoSnapshotView
            {
                Snapshots = new Dictionary<long, ScanRootSnapshotView>
                {
                    [1] = MakeRoot(scanRootId: 1, dirId: 10, hash: h1, size: 100)
                },
                ScanRoots = null!
            };

            plugin.Post(new BootstrapEvent { Generation = 1, RepoSnapshotView = snap1 });
            await plugin.WhenReadyAsync(CancellationToken.None);

            Assert.Equal(1, plugin.TotalDuplicateFileCount);

            // Create a different snapshot but send event with stale generation (1)
            var h2 = NewHash(2);
            var snap2 = new RepoSnapshotView
            {
                Snapshots = new Dictionary<long, ScanRootSnapshotView>
                {
                    [1] = MakeRoot3Files(scanRootId: 1, dirId: 10, hash: h2, size: 777)
                },
                ScanRoots = null!
            };

            plugin.Post(MakeSnapshotCommittedEvent(gen: 1, scanRootId: 1, snapshot: snap2));

            // Wait briefly and assert it didn't change
            await Task.Delay(150, TestContext.Current.CancellationToken);
            Assert.Equal(1, plugin.TotalDuplicateFileCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static ScanRootSnapshotView MakeRoot(long scanRootId, long dirId, HashKey hash, long size)
    {
        // Minimal root:
        // - one directory (index 0)
        // - two files in that directory (indices 0,1) with the same hash/size

        var pool = PackedStringPool.FromStrings(["root", "", "a.bin", "", "b.bin", ""]);

        var dirs = new[]
        {
            new DirRecordV2
            {
                DirId = dirId,
                ParentDirId = -1,
                NameStrIdx = 0,
                ErrorMessageStrIdx = 1,
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                CreatedTicks = 0,
                ModifiedTicks = 0
            }
        };

        var files = new[]
        {
            new FileRecordV2
            {
                FileId = scanRootId * 1000 + 1,
                DirId = dirId,
                NameStrIdx = 2,
                ErrorMessageStrIdx = 3,
                Size = size,
                Hash = hash,
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                CreatedTicks = 0,
                ModifiedTicks = 0
            },
            new FileRecordV2
            {
                FileId = scanRootId * 1000 + 2,
                DirId = dirId,
                NameStrIdx = 4,
                ErrorMessageStrIdx = 5,
                Size = size,
                Hash = hash,
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                CreatedTicks = 0,
                ModifiedTicks = 0
            }
        };

        return new ScanRootSnapshotView
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs = dirs,
            Files = files
        };
    }

    private static ScanRootSnapshotView MakeRoot3Files(long scanRootId, long dirId, HashKey hash, long size)
    {
        // One dir, three files => duplicate count = 2
        var pool = PackedStringPool.FromStrings(["root", "", "a.bin", "", "b.bin", "", "c.bin", ""]);

        var dirs = new[]
        {
            new DirRecordV2
            {
                DirId = dirId,
                ParentDirId = -1,
                NameStrIdx = 0,
                ErrorMessageStrIdx = 1,
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                CreatedTicks = 0,
                ModifiedTicks = 0
            }
        };

        var files = new[]
        {
            new FileRecordV2
            {
                FileId = scanRootId * 1000 + 1,
                DirId = dirId,
                NameStrIdx = 2,
                ErrorMessageStrIdx = 3,
                Size = size,
                Hash = hash,
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                CreatedTicks = 0,
                ModifiedTicks = 0
            },
            new FileRecordV2
            {
                FileId = scanRootId * 1000 + 2,
                DirId = dirId,
                NameStrIdx = 4,
                ErrorMessageStrIdx = 5,
                Size = size,
                Hash = hash,
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                CreatedTicks = 0,
                ModifiedTicks = 0
            },
            new FileRecordV2
            {
                FileId = scanRootId * 1000 + 3,
                DirId = dirId,
                NameStrIdx = 6,
                ErrorMessageStrIdx = 7,
                Size = size,
                Hash = hash,
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                CreatedTicks = 0,
                ModifiedTicks = 0
}
        };

        return new ScanRootSnapshotView
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs = dirs,
            Files = files
        };
    }
}
