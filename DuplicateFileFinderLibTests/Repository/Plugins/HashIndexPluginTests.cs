using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

using DuplicateFileFinderLibTests.TestUtils;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Plugins;

public sealed class HashIndexPluginTests
{
    private readonly TempFsFixture _fs = new("hash-index");

    private sealed class StubTreeIndex : ITreeIndexReadModel
    {
        public ReadOnlySpan<DirHandle> GetChildDirs(DirHandle dir) => ReadOnlySpan<DirHandle>.Empty;
        public ReadOnlySpan<FileHandle> GetChildFiles(DirHandle dir) => ReadOnlySpan<FileHandle>.Empty;

        public DirAggregateStats GetDirStats(DirHandle dir) => new()
        {
            DirCount = 0,
            FileCount = 0,
            TotalBytes = 0,
            DuplicateFiles = 0,
            DuplicateBytes = 0
        };

        public bool TryGetSubtreeRange(DirHandle dir, out SubtreeRange range)
        {
            range = default;
            return false;
        }

        public bool TryGetFileDirPreorder(FileHandle file, out int preorder)
        {
            preorder = -1;
            return false;
        }
    }

    private static HashKey NewHash(int seed)
    {
        var bytes = new byte[16];
        new Random(seed).NextBytes(bytes);
        return new HashKey(bytes);
    }

    private static RepoEvent MakeBootstrapEvent(long gen, RepoSnapshotView snapshot)
        => new BootstrapEvent { Generation = gen, RepoSnapshotView = snapshot };

    private static RepoEvent MakeSnapshotReplacedEvent(long gen, ScanRootId scanRootId, RepoSnapshotView snapshot)
        => new ScanRootSnapshotReplacedEvent
        {
            Generation = gen,
            ScanRootId = scanRootId,
            RepoSnapshotView = snapshot,
            Reason = RepoSnapshotCommitReason.ScanCompleted
        };

    private static IReadOnlyDictionary<ScanRootId, ScanRoot> BuildScanRoots(
        IReadOnlyDictionary<ScanRootId, ScanRootSnapshotView> snapshots,
        HashSet<ScanRootId>? deletedScanRoots = null)
    {
        var dict = new Dictionary<ScanRootId, ScanRoot>(snapshots.Count);

        foreach (var (rootId, s) in snapshots)
        {
            // DirId is required; prefer first dir if present, else 0.
            var dirId = s.Dirs.Count > 0 ? s.Dirs[0].DirId : 0;

            dict[rootId] = new ScanRoot
            {
                RootId = rootId,
                RootPath = $"root-{rootId}",
                DirId = dirId,
                CreatedAt = DateTimeOffset.UtcNow,
                IsDeleted = deletedScanRoots?.Contains(rootId) == true
            };
        }

        return dict;
    }

    private static RepoSnapshotView BuildRepoSnapshot(params ScanRootSnapshotView[] roots)
    {
        var snaps = roots.ToDictionary(x => x.ScanRootId, x => x);
        return new RepoSnapshotView { Snapshots = snaps, ScanRoots = BuildScanRoots(snaps) };
    }

    private static async Task PostAndWaitAsync(
        HashIndexPlugin plugin,
        RepoEvent evt,
        Func<bool>? predicate = null,
        int timeoutMs = 2000)
    {
        plugin.Post(evt);
        if (predicate == null)
        {
            await plugin.WhenReadyAsync(TestContext.Current.CancellationToken);
            return;
        }

        var stop = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < stop)
        {
            if (predicate())
                return;

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Timed out waiting for plugin to process event.");
    }

    // ---------------------------------------------------------------------
    // Core behavioural tests (read model)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Bootstrap_BuildsGroups_AndStats()
    {
        var hDup = NewHash(1);
        var hUnique = NewHash(2);

        // Root 1:
        // - hDup appears twice (size 100) => duplicates: 1 file, wasted: 100
        // - hUnique appears once
        var r1 = MakeRootFromFiles(
            scanRootId: 1,
            dirId: 10,
            files:
            [
                ("a.bin", 100, hDup, ScanEntryStatus.Hashed),
                ("b.bin", 100, hDup, ScanEntryStatus.Hashed),
                ("c.bin", 999, hUnique, ScanEntryStatus.Hashed),
            ]);

        var snapshot = BuildRepoSnapshot(r1);

        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());

        await PostAndWaitAsync(plugin, MakeBootstrapEvent(gen: 1, snapshot));

        Assert.Equal(1, plugin.TotalDuplicateFileCount);
        Assert.Equal(100, plugin.TotalSpaceTakenByDuplicates);

        var page = plugin.GetGroupsPage(
            new DuplicateQuery { MinDuplicates = 2, MinSize = 1, Sort = DuplicateSort.TotalSizeDesc },
            offset: 0,
            count: 10);

        Assert.Equal(0, page.Offset);
        Assert.Equal(1, page.Count);

        var g = Assert.Single(page.Groups.ToArray());
        Assert.Equal(hDup, g.Hash);
        Assert.Equal(100, g.FileSizeBytes);
        Assert.Equal(2, g.Count);

        var handles = plugin.GetGroupFiles(g).ToArray();
        Assert.Equal(2, handles.Length);
        Assert.Contains(new FileHandle(1, 0), handles);
        Assert.Contains(new FileHandle(1, 1), handles);
    }

    [Fact]
    public async Task GetGroupsPage_TotalSizeDesc_SortsAndPages()
    {
        var hA = NewHash(1); // size 10, count 5 => total 50
        var hB = NewHash(2); // size 40, count 2 => total 80   (should rank above A)
        var hC = NewHash(3); // size 30, count 3 => total 90   (should rank above B)
        var hD = NewHash(4); // size 1,  count 10 => total 10  (should rank last)

        var r1 = MakeRootFromGroups(
            scanRootId: 1,
            dirId: 10,
            groups:
            [
                (hA, size: 10, count: 5),
                (hB, size: 40, count: 2),
                (hC, size: 30, count: 3),
                (hD, size: 1, count: 10),
            ]);

        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());
        await PostAndWaitAsync(plugin, MakeBootstrapEvent(1, BuildRepoSnapshot(r1)));

        var rm = (IHashIndexReadModel)plugin;

        // page 0,2 => expect C then B (90, 80)
        var page0 = rm.GetGroupsPage(
            new DuplicateQuery { MinDuplicates = 2, MinSize = 1, Sort = DuplicateSort.TotalSizeDesc },
            offset: 0,
            count: 2);

        Assert.Equal(2, page0.Count);
        Assert.Equal([hC, hB], page0.Groups.ToArray().Select(x => x.Hash).ToArray());

        // page 2,2 => expect A then D (50, 10)
        var page1 = rm.GetGroupsPage(
            new DuplicateQuery { MinDuplicates = 2, MinSize = 1, Sort = DuplicateSort.TotalSizeDesc },
            offset: 2,
            count: 2);

        Assert.Equal(2, page1.Count);
        Assert.Equal([hA, hD], page1.Groups.ToArray().Select(x => x.Hash).ToArray());
    }

    [Fact]
    public async Task GetGroupsPage_DuplicateCountDesc_SortsByCountThenTotalBytes()
    {
        var hA = NewHash(1); // count 5, total 50
        var hB = NewHash(2); // count 5, total 200 (should win tie on count)
        var hC = NewHash(3); // count 3

        var r1 = MakeRootFromGroups(
            scanRootId: 1,
            dirId: 10,
            groups:
            [
                (hA, size: 10, count: 5),
                (hB, size: 40, count: 5),
                (hC, size: 999, count: 3),
            ]);

        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());
        await PostAndWaitAsync(plugin, MakeBootstrapEvent(1, BuildRepoSnapshot(r1)));

        var rm = (IHashIndexReadModel)plugin;

        var page = rm.GetGroupsPage(
            new DuplicateQuery { MinDuplicates = 2, MinSize = 1, Sort = DuplicateSort.DuplicateCountDesc },
            offset: 0,
            count: 10);

        Assert.Equal([hB, hA, hC], page.Groups.ToArray().Select(x => x.Hash).ToArray());
    }

    [Fact]
    public async Task Filters_MinDuplicates_And_MinSize_Work_AndEarlyExitDoesNotMissResults()
    {
        // Ensure we have a descending-by-total set where filtering by MinSize
        // will early-exit once totals dip below threshold.
        var hBig = NewHash(1); // total 1000
        var hMid = NewHash(2); // total 200
        var hSmall = NewHash(3); // total 50

        var r1 = MakeRootFromGroups(
            scanRootId: 1,
            dirId: 10,
            groups:
            [
                (hBig, size: 100, count: 10),
                (hMid, size: 100, count: 2),
                (hSmall, size: 10, count: 5),
            ]);

        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());
        await PostAndWaitAsync(plugin, MakeBootstrapEvent(1, BuildRepoSnapshot(r1)));

        var rm = (IHashIndexReadModel)plugin;

        // MinSize 201 => should include only hBig (1000), exclude hMid (200) and hSmall (50).
        var page = rm.GetGroupsPage(
            new DuplicateQuery { MinDuplicates = 2, MinSize = 201, Sort = DuplicateSort.TotalSizeDesc },
            offset: 0,
            count: 10);

        var g = Assert.Single(page.Groups.ToArray());
        Assert.Equal(hBig, g.Hash);

        // MinDuplicates 6 => only hBig (10) and hSmall (5 excluded) and hMid(2 excluded)
        var page2 = rm.GetGroupsPage(
            new DuplicateQuery { MinDuplicates = 6, MinSize = 1, Sort = DuplicateSort.DuplicateCountDesc },
            offset: 0,
            count: 10);

        var g2 = Assert.Single(page2.Groups.ToArray());
        Assert.Equal(hBig, g2.Hash);
    }

    [Fact]
    public async Task GetGroupFiles_ReturnsEmpty_ForInvalidDescriptor()
    {
        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());
        await PostAndWaitAsync(plugin, MakeBootstrapEvent(1, BuildRepoSnapshot())); // empty repo

        var rm = (IHashIndexReadModel)plugin;

        // nonsense descriptor
        var d = new HashGroupDescriptor(
            Hash: NewHash(1),
            FileSizeBytes: 1,
            Offset: -1,
            Count: 2,
            FirstFile: FileHandle.Invalid);

        Assert.Empty(rm.GetGroupFiles(d).ToArray());
    }

    [Fact]
    public async Task Ignores_NotComputed_CannotCompute_Deleted_None_And_ZeroSize()
    {
        var h = NewHash(1);

        var r1 = MakeRootFromFiles(
            scanRootId: 1,
            dirId: 10,
            files:
            [
                ("a.bin", 100, h, ScanEntryStatus.Hashed), // keep
                ("b.bin", 100, HashKey.NotComputed, ScanEntryStatus.Hashed), // ignore
                ("c.bin", 100, HashKey.CannotCompute, ScanEntryStatus.Hashed), // ignore
                ("d.bin", 100, h, ScanEntryStatus.Deleted), // ignore
                ("e.bin", 100, h, ScanEntryStatus.None), // ignore
                ("f.bin", 0, h, ScanEntryStatus.Hashed), // ignore (size <= 0)
                ("g.bin", 100, h, ScanEntryStatus.Hashed), // keep (duplicate w/ a)
            ]);

        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());
        await PostAndWaitAsync(plugin, MakeBootstrapEvent(1, BuildRepoSnapshot(r1)));

        Assert.Equal(1, plugin.TotalDuplicateFileCount);
        Assert.Equal(100, plugin.TotalSpaceTakenByDuplicates);

        var rm = (IHashIndexReadModel)plugin;
        var page = rm.GetGroupsPage(
            new DuplicateQuery { MinDuplicates = 2, MinSize = 1, Sort = DuplicateSort.TotalSizeDesc },
            0, 10);

        var g = Assert.Single(page.Groups.ToArray());
        Assert.Equal(2, g.Count);
        Assert.Equal(h, g.Hash);

        var handles = rm.GetGroupFiles(g).ToArray();
        Assert.Equal(2, handles.Length);
        Assert.Contains(new FileHandle(1, 0), handles);
        Assert.Contains(new FileHandle(1, 6), handles);
    }

    // ---------------------------------------------------------------------
    // Subtree filtering (owned by HashIndexPlugin)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetGroupsPage_FilteredBySubtree_ReturnsOnlyGroupsIntersectingThatSubtree()
    {
        // Tree: root(0) -> subA(1), subB(2)
        // - Group hA has one file under subA and one under subB => intersects subA
        // - Group hB has both files under subB only => does NOT intersect subA
        var hA = NewHash(1);
        var hB = NewHash(2);

        ScanRootId rootId = 1;
        DirId dirRootId = 10;
        DirId dirAId = 11;
        DirId dirBId = 12;

        // String pool: file names + "" terminator
        var strings = new[] { "a1.bin", "a2.bin", "b1.bin", "b2.bin", "" };
        var emptyIdx = strings.Length - 1;
        var pool = PackedStringPool.FromStrings(strings);

        var dirs = new[]
        {
            new DirRecordV2
            {
                DirId = dirRootId,
                ParentDirId = -1,
                NameStrIdx = emptyIdx,
                ErrorMessageStrIdx = emptyIdx,
                Status = ScanEntryStatus.Enumerated
            },
            new DirRecordV2
            {
                DirId = dirAId,
                ParentDirId = dirRootId,
                NameStrIdx = emptyIdx,
                ErrorMessageStrIdx = emptyIdx,
                Status = ScanEntryStatus.Enumerated
            },
            new DirRecordV2
            {
                DirId = dirBId,
                ParentDirId = dirRootId,
                NameStrIdx = emptyIdx,
                ErrorMessageStrIdx = emptyIdx,
                Status = ScanEntryStatus.Enumerated
            },
        };

        var files = new[]
        {
            // hA: one in subA (index 0), one in subB (index 1)
            new FileRecordV2
            {
                FileId = 100,
                DirId = dirAId,
                NameStrIdx = 0,
                ErrorMessageStrIdx = emptyIdx,
                Size = 100,
                Hash = hA,
                Status = ScanEntryStatus.Hashed
            },
            new FileRecordV2
            {
                FileId = 101,
                DirId = dirBId,
                NameStrIdx = 1,
                ErrorMessageStrIdx = emptyIdx,
                Size = 100,
                Hash = hA,
                Status = ScanEntryStatus.Hashed
            },

            // hB: both in subB (index 2,3)
            new FileRecordV2
            {
                FileId = 200,
                DirId = dirBId,
                NameStrIdx = 2,
                ErrorMessageStrIdx = emptyIdx,
                Size = 50,
                Hash = hB,
                Status = ScanEntryStatus.Hashed
            },
            new FileRecordV2
            {
                FileId = 201,
                DirId = dirBId,
                NameStrIdx = 3,
                ErrorMessageStrIdx = emptyIdx,
                Size = 50,
                Hash = hB,
                Status = ScanEntryStatus.Hashed
            },
        };

        var snap = new ScanRootSnapshotView { ScanRootId = rootId, StringPool = pool, Dirs = dirs, Files = files };

        var repo = BuildRepoSnapshot(snap);

        var treeDir = Path.Combine(_fs.Root, "tree_" + Guid.NewGuid());
        Directory.CreateDirectory(treeDir);

        var hashDir = Path.Combine(_fs.Root, "hash_" + Guid.NewGuid());
        Directory.CreateDirectory(hashDir);

        await using var tree = new TreeIndexPlugin(treeDir);
        tree.Post(MakeBootstrapEvent(1, repo));
        await tree.WhenReadyAsync(TestContext.Current.CancellationToken);

        // subA is dir index 1 in dirs[]
        var subAHandle = new DirHandle(rootId, 1);
        Assert.True(tree.TryGetSubtreeRange(subAHandle, out var range));
        Assert.False(range.IsEmpty);

        var filter = new SubtreeFilter(subAHandle, range);

        await using var hash = new HashIndexPlugin(hashDir, tree);
        await PostAndWaitAsync(hash, MakeBootstrapEvent(1, repo));

        var page = hash.GetGroupsPage(
            new DuplicateQuery { MinDuplicates = 2, MinSize = 1, Sort = DuplicateSort.TotalSizeDesc },
            filter,
            offset: 0,
            count: 10);

        // Only hA intersects subA
        var g = Assert.Single(page.Groups.ToArray());
        Assert.Equal(hA, g.Hash);
        Assert.Equal(2, g.Count);
    }

    // ---------------------------------------------------------------------
    // Generation gating & scan root removal
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ScanRootSnapshotCommittedEvent_IsIgnored_WhenGenerationDoesNotIncrease()
    {
        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());

        var h1 = NewHash(1);
        var snap1 = BuildRepoSnapshot(MakeRootFromGroups(
            scanRootId: 1,
            dirId: 10,
            groups: [(h1, size: 100, count: 2)]));

        await PostAndWaitAsync(plugin, MakeBootstrapEvent(gen: 2, snap1));

        Assert.Equal(1, plugin.TotalDuplicateFileCount);
        Assert.Equal(100, plugin.TotalSpaceTakenByDuplicates);

        // Different snapshot but stale generation (2 again) => ignored
        var h2 = NewHash(2);
        var snap2 = BuildRepoSnapshot(MakeRootFromGroups(
            scanRootId: 1,
            dirId: 10,
            groups: [(h2, size: 777, count: 3)]));

        plugin.Post(MakeSnapshotReplacedEvent(gen: 2, scanRootId: 1, snapshot: snap2));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, plugin.TotalDuplicateFileCount);
        Assert.Equal(100, plugin.TotalSpaceTakenByDuplicates);

        var rm = (IHashIndexReadModel)plugin;
        var page = rm.GetGroupsPage(new DuplicateQuery(), 0, 10);
        var g = Assert.Single(page.Groups.ToArray());
        Assert.Equal(h1, g.Hash);
    }

    [Fact]
    public async Task RepoScanRootRemovedEvent_RebuildsExcludingRemovedRoot()
    {
        // Two roots share same hash group: 2 files per root => 4 handles total
        // duplicates: (4-1)=3, space: 3*100=300
        var h = NewHash(1);

        var r1 = MakeRootFromGroups(scanRootId: 1, dirId: 10, groups: [(h, 100, 2)]);
        var r2 = MakeRootFromGroups(scanRootId: 2, dirId: 20, groups: [(h, 100, 2)]);

        var snaps = new Dictionary<ScanRootId, ScanRootSnapshotView> { [1] = r1, [2] = r2 };
        var snapshot = new RepoSnapshotView { Snapshots = snaps, ScanRoots = BuildScanRoots(snaps) };

        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());

        await PostAndWaitAsync(plugin, MakeBootstrapEvent(1, snapshot));

        Assert.Equal(3, plugin.TotalDuplicateFileCount);
        Assert.Equal(300, plugin.TotalSpaceTakenByDuplicates);

        // Remove root 2: remaining group has count 2 => duplicates: 1, space: 100
        await PostAndWaitAsync(
            plugin,
            new RepoScanRootRemovedEvent(2, 2),
            predicate: () => plugin is { TotalDuplicateFileCount: 1, TotalSpaceTakenByDuplicates: 100 });

        Assert.Equal(1, plugin.TotalDuplicateFileCount);
        Assert.Equal(100, plugin.TotalSpaceTakenByDuplicates);

        var rm = (IHashIndexReadModel)plugin;
        var page = rm.GetGroupsPage(new DuplicateQuery(), 0, 10);
        var g = Assert.Single(page.Groups.ToArray());
        Assert.Equal(2, g.Count);

        var files = rm.GetGroupFiles(g).ToArray();
        Assert.Equal(2, files.Length);
        Assert.All(files, fh => Assert.Equal(1, fh.ScanRootId));
    }

    // ---------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------

    [Fact]
    public async Task State_IsPersisted_And_Rehydrated_OnBootstrap_WhenGenerationMatches()
    {
        var stateDir = Path.Combine(_fs.Root, "persist_" + Guid.NewGuid());
        Directory.CreateDirectory(stateDir);

        var h = NewHash(1);
        var snapshot = BuildRepoSnapshot(MakeRootFromGroups(scanRootId: 1, dirId: 10, groups: [(h, 123, 4)]));

        // Build + persist
        await using (var plugin = new HashIndexPlugin(stateDir, new StubTreeIndex()))
        {
            await PostAndWaitAsync(plugin, MakeBootstrapEvent(5, snapshot));

            Assert.Equal(3, plugin.TotalDuplicateFileCount); // (4-1)
            Assert.Equal(3 * 123, plugin.TotalSpaceTakenByDuplicates);
        }

        // New instance should load state on bootstrap (gen must match)
        await using (var plugin2 = new HashIndexPlugin(stateDir, new StubTreeIndex()))
        {
            await PostAndWaitAsync(plugin2, MakeBootstrapEvent(5, snapshot));

            Assert.Equal(3, plugin2.TotalDuplicateFileCount);
            Assert.Equal(3 * 123, plugin2.TotalSpaceTakenByDuplicates);

            var rm = (IHashIndexReadModel)plugin2;
            var page = rm.GetGroupsPage(new DuplicateQuery(), 0, 10);
            var g = Assert.Single(page.Groups.ToArray());
            Assert.Equal(4, g.Count);
            Assert.Equal(123, g.FileSizeBytes);

            var files = rm.GetGroupFiles(g).ToArray();
            Assert.Equal(4, files.Length);
        }
    }

    [Fact]
    public async Task RepoFileDeletedEvent_RemovesSingleFile_FromGroup_AndUpdatesStats()
    {
        var h = NewHash(1);

        // One duplicate group of 3 files => duplicates: 2, wasted: 200
        var snapshot = BuildRepoSnapshot(
            MakeRootFromGroups(scanRootId: 1, dirId: 10, groups: [(h, 100, 3)]));

        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());

        await PostAndWaitAsync(plugin, MakeBootstrapEvent(1, snapshot));

        Assert.Equal(2, plugin.TotalDuplicateFileCount);
        Assert.Equal(200, plugin.TotalSpaceTakenByDuplicates);

        await PostAndWaitAsync(
            plugin,
            new RepoFileDeletedEvent { Generation = 2, ScanRootId = 1, File = new FileHandle(1, 0), FileId = 1001 },
            predicate: () => plugin is { TotalDuplicateFileCount: 1, TotalSpaceTakenByDuplicates: 100 });

        Assert.Equal(1, plugin.TotalDuplicateFileCount);
        Assert.Equal(100, plugin.TotalSpaceTakenByDuplicates);

        var rm = (IHashIndexReadModel)plugin;
        var page = rm.GetGroupsPage(new DuplicateQuery(), 0, 10);
        var g = Assert.Single(page.Groups.ToArray());

        Assert.Equal(h, g.Hash);
        Assert.Equal(2, g.Count);
        Assert.Equal(100, g.FileSizeBytes);

        var files = rm.GetGroupFiles(g).ToArray();
        Assert.Equal(2, files.Length);
        Assert.DoesNotContain(new FileHandle(1, 0), files);
        Assert.Contains(new FileHandle(1, 1), files);
        Assert.Contains(new FileHandle(1, 2), files);
    }

    [Fact]
    public async Task RepoFileDeletedEvent_RemovingOneOfTwo_RemovesGroupEntirely()
    {
        var h = NewHash(1);

        // One duplicate group of 2 files => duplicates: 1, wasted: 100
        var snapshot = BuildRepoSnapshot(
            MakeRootFromGroups(scanRootId: 1, dirId: 10, groups: [(h, 100, 2)]));

        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());

        await PostAndWaitAsync(plugin, MakeBootstrapEvent(1, snapshot));

        Assert.Equal(1, plugin.TotalDuplicateFileCount);
        Assert.Equal(100, plugin.TotalSpaceTakenByDuplicates);

        await PostAndWaitAsync(
            plugin,
            new RepoFileDeletedEvent { Generation = 2, ScanRootId = 1, File = new FileHandle(1, 0), FileId = 1001 },
            predicate: () => plugin is { TotalDuplicateFileCount: 0, TotalSpaceTakenByDuplicates: 0 });

        Assert.Equal(0, plugin.TotalDuplicateFileCount);
        Assert.Equal(0, plugin.TotalSpaceTakenByDuplicates);

        var rm = (IHashIndexReadModel)plugin;
        var page = rm.GetGroupsPage(new DuplicateQuery(), 0, 10);
        Assert.Equal(0, page.Count);
        Assert.Empty(page.Groups.ToArray());
    }

    [Fact]
    public async Task RepoDirDeletedEvent_RemovesOnlySpecifiedFiles_AndKeepsOtherGroups()
    {
        var hA = NewHash(1);
        var hB = NewHash(2);

        // hA: 3 files of size 100 => duplicates: 2, wasted: 200
        // hB: 2 files of size 50  => duplicates: 1, wasted: 50
        var snapshot = BuildRepoSnapshot(
            MakeRootFromGroups(
                scanRootId: 1,
                dirId: 10,
                groups:
                [
                    (hA, 100, 3),
                    (hB, 50, 2),
                ]));

        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());

        await PostAndWaitAsync(plugin, MakeBootstrapEvent(1, snapshot));

        Assert.Equal(3, plugin.TotalDuplicateFileCount);
        Assert.Equal(250, plugin.TotalSpaceTakenByDuplicates);

        // Remove two files from the hA group: file ids 1001, 1002 (indices 0,1)
        await PostAndWaitAsync(
            plugin,
            new RepoDirDeletedEvent
            {
                Generation = 2,
                ScanRootId = 1,
                Dir = new DirHandle(1, 0),
                DeletedDirIds = [10],
                DeletedFiles = [
                    (1001, new FileHandle(1, 1)),
                    (1002, new FileHandle(1, 2))]
            },
            predicate: () => plugin is { TotalDuplicateFileCount: 1, TotalSpaceTakenByDuplicates: 50 });

        Assert.Equal(1, plugin.TotalDuplicateFileCount);
        Assert.Equal(50, plugin.TotalSpaceTakenByDuplicates);

        var rm = (IHashIndexReadModel)plugin;
        var page = rm.GetGroupsPage(
            new DuplicateQuery { MinDuplicates = 2, MinSize = 1, Sort = DuplicateSort.TotalSizeDesc },
            0,
            10);

        var groups = page.Groups.ToArray();
        Assert.Single(groups);

        var remaining = groups[0];
        Assert.Equal(hB, remaining.Hash);
        Assert.Equal(2, remaining.Count);
        Assert.Equal(50, remaining.FileSizeBytes);

        var handles = rm.GetGroupFiles(remaining).ToArray();
        Assert.Equal(2, handles.Length);
        Assert.All(handles, fh => Assert.Equal(1, fh.ScanRootId));
        Assert.Contains(new FileHandle(1, 3), handles);
        Assert.Contains(new FileHandle(1, 4), handles);
    }

    [Fact]
    public async Task RepoDirDeletedEvent_RemovesSubsetOfGroup_AndCompactsHandles()
    {
        var h = NewHash(1);

        // 4 files => duplicates: 3, wasted: 300
        var snapshot = BuildRepoSnapshot(
            MakeRootFromGroups(scanRootId: 1, dirId: 10, groups: [(h, 100, 4)]));

        await using var plugin = new HashIndexPlugin(_fs.Root, new StubTreeIndex());

        await PostAndWaitAsync(plugin, MakeBootstrapEvent(1, snapshot));

        Assert.Equal(3, plugin.TotalDuplicateFileCount);
        Assert.Equal(300, plugin.TotalSpaceTakenByDuplicates);

        // Remove files at indices 1 and 3 => ids 1002 and 1004
        await PostAndWaitAsync(
            plugin,
            new RepoDirDeletedEvent
            {
                Generation = 2,
                ScanRootId = 1,
                Dir = new DirHandle(1, 0),
                DeletedDirIds = Array.Empty<DirId>(),
                DeletedFiles = [
                    (1002, new FileHandle(1, 1)),
                    (1004, new FileHandle(1, 3))]
            },
            predicate: () => plugin is { TotalDuplicateFileCount: 1, TotalSpaceTakenByDuplicates: 100 });

        Assert.Equal(1, plugin.TotalDuplicateFileCount);
        Assert.Equal(100, plugin.TotalSpaceTakenByDuplicates);

        var rm = (IHashIndexReadModel)plugin;
        var page = rm.GetGroupsPage(new DuplicateQuery(), 0, 10);
        var g = Assert.Single(page.Groups.ToArray());

        Assert.Equal(h, g.Hash);
        Assert.Equal(2, g.Count);

        var files = rm.GetGroupFiles(g).ToArray();
        Assert.Equal(2, files.Length);
        Assert.Equal([new FileHandle(1, 0), new FileHandle(1, 2)], files);
    }

    // ---------------------------------------------------------------------
    // Local snapshot builders
    // ---------------------------------------------------------------------

    private static ScanRootSnapshotView MakeRootFromGroups(
        ScanRootId scanRootId,
        DirId dirId,
        (HashKey hash, long size, int count)[] groups)
    {
        var files = new List<(string name, long size, HashKey hash, ScanEntryStatus status)>();

        var i = 0;
        foreach (var g in groups)
        {
            for (var j = 0; j < g.count; j++)
                files.Add(($"f{i++}.bin", g.size, g.hash, ScanEntryStatus.Hashed));
        }

        return MakeRootFromFiles(scanRootId, dirId, files.ToArray());
    }

    private static ScanRootSnapshotView MakeRootFromFiles(
        ScanRootId scanRootId,
        DirId dirId,
        (string name, long size, HashKey hash, ScanEntryStatus status)[] files)
    {
        // String pool: all file names + a single "" entry at end used as ErrorMessageStrIdx and Dir name
        var strings = files.Select(f => f.name).Concat([""]).ToArray();
        var emptyIdx = strings.Length - 1;

        var pool = PackedStringPool.FromStrings(strings);

        var fileRecords = new FileRecordV2[files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            var f = files[i];
            fileRecords[i] = new FileRecordV2
            {
                FileId = scanRootId * 1000 + i + 1,
                DirId = dirId,
                NameStrIdx = i,
                ErrorMessageStrIdx = emptyIdx,
                Size = f.size,
                Hash = f.hash,
                Status = f.status
            };
        }

        var dirs = new[]
        {
            new DirRecordV2
            {
                DirId = dirId,
                ParentDirId = -1,
                NameStrIdx = emptyIdx,
                ErrorMessageStrIdx = emptyIdx,
                Status = ScanEntryStatus.Enumerated
            }
        };

        return new ScanRootSnapshotView
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs = dirs,
            Files = fileRecords
        };
    }
}
