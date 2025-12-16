using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins;
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
            NextLogSequence = 1,
            RepoSnapshotView = snapshot
        };
    }

    [Fact]
    public async Task OpenedEvent_BuildsInitialDuplicateGroups()
    {
        var hashDup = NewHash(1);
        var hashUnique = NewHash(2);

        var files = new FileRecord[]
        {
            new()
            {
                FileId = 1,
                DirId = 10,
                Name = "a.bin",
                Size = 100,
                Hash = hashDup,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            },

            new()
            {
                FileId = 2,
                DirId = 11,
                Name = "b.bin",
                Size = 100,
                Hash = hashDup,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            },

            new()
            {
                FileId = 3,
                DirId = 12,
                Name = "c.bin",
                Size = 100,
                Hash = hashUnique,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            }
        };

        var snapshot = RepoUtil.MakeSnapshot(1, [], files);

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

        var files = new FileRecord[]
        {
            new()
            {
                FileId = 1,
                DirId = 10,
                Name = "a.bin",
                Size = 100,
                Hash = defaultHash,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            },

            new()
            {
                FileId = 2,
                DirId = 11,
                Name = "b.bin",
                Size = 100,
                Hash = defaultHash,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            }
        };

        var snapshot = RepoUtil.MakeSnapshot(1, [], files);

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
                ScanRoots = null
            };

            plugin.Post(new BootstrapEvent
            {
                Generation = 1,
                NextLogSequence = 10,
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
}