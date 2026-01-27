// DuplicateFileFinderLibTests/Repository/Plugins/FolderHashIndexPluginTests.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

public sealed class FolderHashIndexPluginTests
{
    private readonly TempFsFixture _fs = new("folder-hash-index");

    private static HashKey NewHash(int seed)
    {
        var bytes = new byte[16];
        new Random(seed).NextBytes(bytes);
        return new HashKey(bytes);
    }

    private static RepoEvent Bootstrap(long gen, RepoSnapshotView snap)
        => new BootstrapEvent { Generation = gen, RepoSnapshotView = snap };

    private static RepoEvent RemoveScanRoot(long gen, long scanRootId)
        => new RepoScanRootRemovedEvent { Generation = gen, ScanRootId = scanRootId };

    private static RepoSnapshotView BuildRepoSnapshot(params ScanRootSnapshotView[] roots)
    {
        var snaps = roots.ToDictionary(x => x.ScanRootId, x => x);

        // ScanRoot only needs RootId/RootPath/DirId/IsDeleted for these plugins.
        var scanRoots = new Dictionary<long, ScanRoot>(snaps.Count);
        foreach (var (rootId, s) in snaps)
        {
            var dirId = s.Dirs.Count > 0 ? s.Dirs[0].DirId : 0;

            scanRoots[rootId] = new ScanRoot
            {
                RootId = rootId,
                RootPath = $"root-{rootId}",
                DirId = dirId,
                CreatedAt = DateTimeOffset.UtcNow,
                IsDeleted = false
            };
        }

        return new RepoSnapshotView
        {
            Snapshots = snaps,
            ScanRoots = scanRoots
        };
    }

    private static ScanRootSnapshotView MakeRoot(
        long scanRootId,
        (long dirId, long parentDirId, ScanEntryStatus status)[] dirs,
        (string name, long fileId, long dirId, long size, HashKey hash, ScanEntryStatus status)[] files)
    {
        // String pool: file names + "" for dir/error fields.
        var strings = files.Select(f => f.name).Concat([""]).ToArray();
        var emptyIdx = strings.Length - 1;

        var pool = PackedStringPool.FromStrings(strings);

        var dirRecords = new DirRecordV2[dirs.Length];
        for (var i = 0; i < dirs.Length; i++)
        {
            var (dirId, parentId, status) = dirs[i];
            dirRecords[i] = new DirRecordV2
            {
                DirId = dirId,
                ParentDirId = parentId,
                NameStrIdx = emptyIdx,
                ErrorMessageStrIdx = emptyIdx,
                Status = status
            };
        }

        var fileRecords = new FileRecordV2[files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            var f = files[i];
            fileRecords[i] = new FileRecordV2
            {
                FileId = f.fileId,
                DirId = f.dirId,
                NameStrIdx = i,
                ErrorMessageStrIdx = emptyIdx,
                Size = f.size,
                Hash = f.hash,
                Status = f.status
            };
        }

        return new ScanRootSnapshotView
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs = dirRecords,
            Files = fileRecords
        };
    }

    private static (string treeDir, string folderDir) CreatePluginDirs(string root)
    {
        var treeDir = Path.Combine(root, "tree_" + Guid.NewGuid());
        var folderDir = Path.Combine(root, "folder_" + Guid.NewGuid());
        Directory.CreateDirectory(treeDir);
        Directory.CreateDirectory(folderDir);
        return (treeDir, folderDir);
    }

    [Fact]
    public async Task Bootstrap_BuildsDuplicateFolderGroups_ForIdenticalDirectChildSets()
    {
        // Structure:
        // root (dir index 0)
        //  - A (dir index 1) contains one file with hash H
        //  - B (dir index 2) contains one file with hash H
        // => A and B have identical direct children => duplicate folder group count=2
        var h = NewHash(1);

        const long rootId = 1;
        const long dirRootId = 10;
        const long dirAId = 11;
        const long dirBId = 12;

        var snap = MakeRoot(
            scanRootId: rootId,
            dirs:
            [
                (dirRootId, -1, ScanEntryStatus.Enumerated),
                (dirAId, dirRootId, ScanEntryStatus.Enumerated),
                (dirBId, dirRootId, ScanEntryStatus.Enumerated)
            ],
            files:
            [
                ("a1.bin", fileId: 100, dirId: dirAId, size: 123, hash: h, status: ScanEntryStatus.Hashed),
                ("b1.bin", fileId: 101, dirId: dirBId, size: 123, hash: h, status: ScanEntryStatus.Hashed)
            ]);

        var repo = BuildRepoSnapshot(snap);

        var (treeDir, folderDir) = CreatePluginDirs(_fs.Root);

        await using var tree = new TreeIndexPlugin(treeDir);
        await PluginTestUtil.PostAndWaitAsync(tree, Bootstrap(1, repo));

        await using var folder = new FolderHashIndexPlugin(folderDir, tree);
        await PluginTestUtil.PostAndWaitAsync(folder, Bootstrap(1, repo));

        Assert.Equal(1, folder.TotalDuplicateFolderCount); // (2-1)

        var page = folder.GetGroupsPage(0, 10, FolderDuplicateSort.DuplicateCountDesc);
        Assert.Equal(1, page.Count);

        var g = Assert.Single(page.Groups.ToArray());
        Assert.Equal(2, g.Count);
        Assert.Equal(1, g.ChildFileCount);
        Assert.Equal(0, g.ChildDirCount);

        var dirs = folder.GetGroupDirs(g).ToArray();
        Assert.Equal(2, dirs.Length);

        // dir indices 1 and 2 correspond to A and B
        Assert.Contains(new DirHandle(rootId, 1), dirs);
        Assert.Contains(new DirHandle(rootId, 2), dirs);
    }

    [Fact]
    public async Task NonEquivalentStructure_DoesNotGroup()
    {
        // A: [H]
        // B: [H, H2] => different direct child set => not duplicates
        var h1 = NewHash(1);
        var h2 = NewHash(2);

        const long rootId = 1;
        const long dirRootId = 10;
        const long dirAId = 11;
        const long dirBId = 12;

        var snap = MakeRoot(
            scanRootId: rootId,
            dirs:
            [
                (dirRootId, -1, ScanEntryStatus.Enumerated),
                (dirAId, dirRootId, ScanEntryStatus.Enumerated),
                (dirBId, dirRootId, ScanEntryStatus.Enumerated)
            ],
            files:
            [
                ("a1.bin", fileId: 100, dirId: dirAId, size: 10, hash: h1, status: ScanEntryStatus.Hashed),

                ("b1.bin", fileId: 101, dirId: dirBId, size: 10, hash: h1, status: ScanEntryStatus.Hashed),
                ("b2.bin", fileId: 102, dirId: dirBId, size: 20, hash: h2, status: ScanEntryStatus.Hashed),
            ]);

        var repo = BuildRepoSnapshot(snap);

        var (treeDir, folderDir) = CreatePluginDirs(_fs.Root);

        await using var tree = new TreeIndexPlugin(treeDir);
        await PluginTestUtil.PostAndWaitAsync(tree, Bootstrap(1, repo));

        await using var folder = new FolderHashIndexPlugin(folderDir, tree);
        await PluginTestUtil.PostAndWaitAsync(folder, Bootstrap(1, repo));

        Assert.Equal(0, folder.TotalDuplicateFolderCount);

        var page = folder.GetGroupsPage(0, 10, FolderDuplicateSort.DuplicateCountDesc);
        Assert.Equal(0, page.Count);
    }

    [Fact]
    public async Task NotComputedFileHash_MakesFolderUncomputable_AndExcludesFromGrouping()
    {
        // A: contains NotComputed => A cannot compute
        // B: computed => singleton => no groups
        var h = NewHash(1);

        const long rootId = 1;
        const long dirRootId = 10;
        const long dirAId = 11;
        const long dirBId = 12;

        var snap = MakeRoot(
            scanRootId: rootId,
            dirs:
            [
                (dirRootId, -1, ScanEntryStatus.Enumerated),
                (dirAId, dirRootId, ScanEntryStatus.Enumerated),
                (dirBId, dirRootId, ScanEntryStatus.Enumerated)
            ],
            files:
            [
                ("a1.bin", fileId: 100, dirId: dirAId, size: 10, hash: HashKey.NotComputed, status: ScanEntryStatus.Hashed),
                ("b1.bin", fileId: 101, dirId: dirBId, size: 10, hash: h, status: ScanEntryStatus.Hashed),
            ]);

        var repo = BuildRepoSnapshot(snap);

        var (treeDir, folderDir) = CreatePluginDirs(_fs.Root);

        await using var tree = new TreeIndexPlugin(treeDir);
        await PluginTestUtil.PostAndWaitAsync(tree, Bootstrap(1, repo));

        await using var folder = new FolderHashIndexPlugin(folderDir, tree);
        await PluginTestUtil.PostAndWaitAsync(folder, Bootstrap(1, repo));

        Assert.Equal(0, folder.TotalDuplicateFolderCount);
        Assert.Equal(0, folder.GetGroupsPage(0, 10, FolderDuplicateSort.DuplicateCountDesc).Count);
    }

    [Fact]
    public async Task ZeroByteFiles_AreIgnored_ForFolderHash_AndDoNotPreventDeterministicHash()
    {
        // Both A and B contain only zero-byte hashed files. Since folder hashing ignores size<=0,
        // both effectively have (childFileCount=0, childDirCount=0) and should group.
        var h1 = NewHash(1);
        var h2 = NewHash(2);

        const long rootId = 1;
        const long dirRootId = 10;
        const long dirAId = 11;
        const long dirBId = 12;

        var snap = MakeRoot(
            scanRootId: rootId,
            dirs:
            [
                (dirRootId, -1, ScanEntryStatus.Enumerated),
                (dirAId, dirRootId, ScanEntryStatus.Enumerated),
                (dirBId, dirRootId, ScanEntryStatus.Enumerated)
            ],
            files:
            [
                ("a0.bin", fileId: 100, dirId: dirAId, size: 0, hash: h1, status: ScanEntryStatus.Hashed),
                ("b0.bin", fileId: 101, dirId: dirBId, size: 0, hash: h2, status: ScanEntryStatus.Hashed),
            ]);

        var repo = BuildRepoSnapshot(snap);

        var (treeDir, folderDir) = CreatePluginDirs(_fs.Root);

        await using var tree = new TreeIndexPlugin(treeDir);
        await PluginTestUtil.PostAndWaitAsync(tree, Bootstrap(1, repo));

        await using var folder = new FolderHashIndexPlugin(folderDir, tree);
        await PluginTestUtil.PostAndWaitAsync(folder, Bootstrap(1, repo));

        Assert.Equal(1, folder.TotalDuplicateFolderCount);

        var page = folder.GetGroupsPage(0, 10, FolderDuplicateSort.DuplicateCountDesc);
        var g = Assert.Single(page.Groups.ToArray());
        Assert.Equal(2, g.Count);

        // Since size<=0 are ignored, the direct eligible child-file count should be 0.
        Assert.Equal(0, g.ChildFileCount);
    }

    [Fact]
    public async Task RepoScanRootRemovedEvent_RemovesDirsFromGroups_AndMayEliminateGroups()
    {
        // Root1 has folder A with [H]
        // Root2 has folder A with [H]
        // => duplicates count = 1
        // Remove root2 => remaining singleton => no groups
        var h = NewHash(1);

        var r1 = MakeRoot(
            scanRootId: 1,
            dirs:
            [
                (10, -1, ScanEntryStatus.Enumerated),  // root
                (11, 10, ScanEntryStatus.Enumerated),  // A
            ],
            files:
            [
                ("a1.bin", fileId: 100, dirId: 11, size: 10, hash: h, status: ScanEntryStatus.Hashed),
            ]);

        var r2 = MakeRoot(
            scanRootId: 2,
            dirs:
            [
                (20, -1, ScanEntryStatus.Enumerated),  // root
                (21, 20, ScanEntryStatus.Enumerated),  // A
            ],
            files:
            [
                ("a1.bin", fileId: 200, dirId: 21, size: 10, hash: h, status: ScanEntryStatus.Hashed),
            ]);

        var repo = BuildRepoSnapshot(r1, r2);

        var (treeDir, folderDir) = CreatePluginDirs(_fs.Root);

        await using var tree = new TreeIndexPlugin(treeDir);
        await PluginTestUtil.PostAndWaitAsync(tree, Bootstrap(1, repo));

        await using var folder = new FolderHashIndexPlugin(folderDir, tree);
        await PluginTestUtil.PostAndWaitAsync(folder, Bootstrap(1, repo),
            predicate: () => folder.TotalDuplicateFolderCount == 2);

        Assert.Equal(2, folder.TotalDuplicateFolderCount);

        // Sanity: we have 2 groups (root group + A group)
        var pageBefore = folder.GetGroupsPage(0, 10, FolderDuplicateSort.DuplicateCountDesc);
        Assert.Equal(2, pageBefore.Count);

        await PluginTestUtil.PostAndWaitAsync(folder, RemoveScanRoot(2, scanRootId: 2),
            predicate: () => folder.TotalDuplicateFolderCount == 0);

        Assert.Equal(0, folder.TotalDuplicateFolderCount);
        Assert.Equal(0, folder.GetGroupsPage(0, 10, FolderDuplicateSort.DuplicateCountDesc).Count);
    }
}
