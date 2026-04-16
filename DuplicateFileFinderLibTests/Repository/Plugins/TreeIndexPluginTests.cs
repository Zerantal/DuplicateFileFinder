using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins;

using DuplicateFileFinderLibTests.TestUtils;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Plugins;

public sealed class TreeIndexPluginTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TreeIndexTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task BootstrapEvent_BuildsInitialTreeIndex_AndPersistsState()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var plugin = new TreeIndexPlugin(tempDir);

            // Layout (DirRecordV2 uses ParentDirId = -1 for "no parent"):
            //   DirId 1: root (parent -1)
            //     DirId 2: subA (parent 1)
            //     DirId 3: subB (parent 1)
            //   Files:
            //     FileId 10 under DirId 1
            //     FileId 20 under DirId 2
            var snap = RepoUtil.MakeSnapshotV2(
                scanRootId: 1,
                dirs:
                [
                    ("root", parentDirId: -1, dirId: 1),
                    ("subA", parentDirId: 1, dirId: 2),
                    ("subB", parentDirId: 1, dirId: 3)
                ],
                files:
                [
                    ("file_root.txt", dirId: 1, fileId: 10, size: 123L),
                    ("file_subA.txt", dirId: 2, fileId: 20, size: 456L)
                ]);

            plugin.Post(new BootstrapEvent { Generation = 1, RepoSnapshotView = snap });

            await plugin.WhenReadyAsync(CancellationToken.None);

            // handles by index in dirs/files arrays above
            var root = new DirHandle(1, 0);
            var subA = new DirHandle(1, 1);
            var subB = new DirHandle(1, 2);

            var fileRoot = new FileHandle(1, 0);
            var fileSubA = new FileHandle(1, 1);

            // root children
            Assert.Equal(
                RepoUtil.Sort([subA, subB]),
                RepoUtil.Sort(plugin.GetChildDirs(root).ToArray()));

            Assert.Equal(
                RepoUtil.Sort([fileRoot]),
                RepoUtil.Sort(plugin.GetChildFiles(root).ToArray()));

            // subA children
            Assert.True(plugin.GetChildDirs(subA).IsEmpty);
            Assert.Equal(
                RepoUtil.Sort([fileSubA]),
                RepoUtil.Sort(plugin.GetChildFiles(subA).ToArray()));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BootstrapEvent_LoadsExistingState_WhenStateMatchesGenerationAndSequence()
    {
        var tempDir = CreateTempDir();
        try
        {
            // First run: build state.
            await using (var plugin1 = new TreeIndexPlugin(tempDir))
            {
                var snap1 = RepoUtil.MakeSnapshotV2(
                    scanRootId: 1,
                    dirs:
                    [
                        ("root", parentDirId: -1, dirId: 1),
                        ("subA", parentDirId: 1, dirId: 2)
                    ],
                    files:
                    [
                        ("file_subA.txt", dirId: 1, fileId: 10, size: 1L)
                    ]);

                plugin1.Post(new BootstrapEvent { Generation = 1, RepoSnapshotView = snap1 });

                await plugin1.WhenReadyAsync(TestContext.Current.CancellationToken);

                var root = new DirHandle(1, 0);
                var subA = new DirHandle(1, 1);
                var fileSubA = new FileHandle(1, 0);

                Assert.Equal(new[] { subA }, plugin1.GetChildDirs(root).ToArray());
                Assert.Equal(new[] { fileSubA }, plugin1.GetChildFiles(root).ToArray());
            }

            // Second run: different snapshot, same generation -> should load persisted state.
            // (Test expects "state load" wins over new snapshot.)
            await using (var plugin2 = new TreeIndexPlugin(tempDir))
            {
                var snap2 = RepoUtil.MakeSnapshotV2(
                    scanRootId: 1,
                    dirs:
                    [
                        ("root", parentDirId: -1, dirId: 1),
                        ("subB", parentDirId: 1, dirId: 3)
                    ],
                    files:
                    [
                        ("fileB.txt", dirId: 1, fileId: 99, size: 2L)
                    ]);

                plugin2.Post(new BootstrapEvent { Generation = 1, RepoSnapshotView = snap2 });

                await plugin2.WhenReadyAsync(TestContext.Current.CancellationToken);

                var root = new DirHandle(1, 0);
                var subA = new DirHandle(1, 1);
                var fileSubA = new FileHandle(1, 0);

                // Should reflect persisted state (dir 2, file 10), not new snapshot (dir 3, file 99)
                Assert.Equal(new[] { subA }, plugin2.GetChildDirs(root).ToArray());
                Assert.Equal(new[] { fileSubA }, plugin2.GetChildFiles(root).ToArray());
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ScanRootSnapshotCommittedEvent_RebuildsTreeIndex_WhenGenerationIncreases()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var plugin = new TreeIndexPlugin(tempDir);

            var snap1 = RepoUtil.MakeSnapshotV2(
                scanRootId: 1,
                dirs:
                [
                    ("root", parentDirId: -1, dirId: 1),
                    ("subA", parentDirId: 1, dirId: 2)
                ],
                files: Array.Empty<(string name, DirId dirId, FileId fileId, long size)>());

            plugin.Post(new BootstrapEvent { Generation = 1, RepoSnapshotView = snap1 });
            await plugin.WhenReadyAsync(CancellationToken.None);

            var rootHandle = new DirHandle(1, 0);
            Assert.Single(plugin.GetChildDirs(rootHandle).ToArray());

            // Snapshot 2: replace subA with subB (still child at index 1 but different DirId behind it)
            var snap2 = RepoUtil.MakeSnapshotV2(
                scanRootId: 1,
                dirs:
                [
                    ("root", parentDirId: -1, dirId: 1),
                    ("subB", parentDirId: 1, dirId: 3)
                ],
                files: Array.Empty<(string name, DirId dirId, FileId fileId, long size)>());

            plugin.Post(new ScanRootSnapshotReplacedEvent
            {
                Generation = 2,
                ScanRootId = 1,
                RepoSnapshotView = snap2,
                Reason = RepoSnapshotCommitReason.ScanCompleted
            });

            await AsyncUtil.WaitForConditionAsync(
                () => plugin.GetChildDirs(rootHandle).Length == 1 &&
                      plugin.GetChildDirs(rootHandle)[0].Index == 1,
                TimeSpan.FromSeconds(2));

            var childDirs2 = plugin.GetChildDirs(rootHandle).ToArray();
            Assert.Single(childDirs2);
            Assert.Equal(new DirHandle(1, 1), childDirs2[0]);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TryGetSubtreeRange_And_TryGetFileDirPreorder_ReturnExpectedMappings()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var plugin = new TreeIndexPlugin(tempDir);

            // root(0) -> subA(1), subB(2)
            // file_subA under subA (file index 1)
            var snap = RepoUtil.MakeSnapshotV2(
                scanRootId: 1,
                dirs:
                [
                    ("root", parentDirId: -1, dirId: 1),
                    ("subA", parentDirId: 1, dirId: 2),
                    ("subB", parentDirId: 1, dirId: 3)
                ],
                files:
                [
                    ("file_root.txt", dirId: 1, fileId: 10, size: 123L),
                    ("file_subA.txt", dirId: 2, fileId: 20, size: 456L)
                ]);

            plugin.Post(new BootstrapEvent { Generation = 1, RepoSnapshotView = snap });
            await plugin.WhenReadyAsync(TestContext.Current.CancellationToken);

            var root = new DirHandle(1, 0);
            var subA = new DirHandle(1, 1);
            var subB = new DirHandle(1, 2);

            Assert.True(plugin.TryGetSubtreeRange(root, out var rRoot));
            Assert.True(plugin.TryGetSubtreeRange(subA, out var rA));
            Assert.True(plugin.TryGetSubtreeRange(subB, out var rB));

            // Deterministic DFS order based on dir enumeration order:
            Assert.Equal(0, rRoot.Start);
            Assert.Equal(1, rA.Start);
            Assert.Equal(2, rB.Start);

            Assert.True(rRoot.Contains(rA.Start));
            Assert.True(rRoot.Contains(rB.Start));

            // File -> parent dir preorder
            var fileRoot = new FileHandle(1, 0);
            var fileSubA = new FileHandle(1, 1);

            Assert.True(plugin.TryGetFileDirPreorder(fileRoot, out var preRootFile));
            Assert.True(plugin.TryGetFileDirPreorder(fileSubA, out var preSubAFile));

            Assert.Equal(rRoot.Start, preRootFile);
            Assert.Equal(rA.Start, preSubAFile);

            Assert.True(rRoot.Contains(preRootFile));
            Assert.True(rRoot.Contains(preSubAFile));
            Assert.True(rA.Contains(preSubAFile));
            Assert.False(rB.Contains(preSubAFile));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RepoFileDeletedEvent_RemovesFileFromParent_AndClearsFilePreorder()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var plugin = new TreeIndexPlugin(tempDir);

            // root(0) -> subA(1), subB(2)
            // files:
            //   file_root.txt under root  (file index 0)
            //   file_subA.txt under subA  (file index 1)
            var snap = RepoUtil.MakeSnapshotV2(
                scanRootId: 1,
                dirs:
                [
                    ("root", parentDirId: -1, dirId: 1),
                    ("subA", parentDirId: 1, dirId: 2),
                    ("subB", parentDirId: 1, dirId: 3)
                ],
                files:
                [
                    ("file_root.txt", dirId: 1, fileId: 10, size: 123L),
                    ("file_subA.txt", dirId: 2, fileId: 20, size: 456L)
                ]);

            plugin.Post(new BootstrapEvent { Generation = 1, RepoSnapshotView = snap });

            await plugin.WhenReadyAsync(TestContext.Current.CancellationToken);

            var root = new DirHandle(1, 0);
            var subA = new DirHandle(1, 1);
            var fileRoot = new FileHandle(1, 0);
            var fileSubA = new FileHandle(1, 1);

            Assert.Equal(new[] { fileRoot }, plugin.GetChildFiles(root).ToArray());
            Assert.Equal(new[] { fileSubA }, plugin.GetChildFiles(subA).ToArray());
            Assert.True(plugin.TryGetFileDirPreorder(fileSubA, out var preBefore));

            plugin.Post(new RepoFileDeletedEvent { Generation = 2, ScanRootId = 1, File = fileSubA, FileId = 20 });

            await AsyncUtil.WaitForConditionAsync(
                () => plugin.GetChildFiles(subA).IsEmpty,
                TimeSpan.FromSeconds(2));

            Assert.Equal(new[] { fileRoot }, plugin.GetChildFiles(root).ToArray());
            Assert.True(plugin.GetChildFiles(subA).IsEmpty);

            Assert.False(plugin.TryGetFileDirPreorder(fileSubA, out _));
            Assert.True(plugin.TryGetFileDirPreorder(fileRoot, out var preRootAfter));
            Assert.NotEqual(preBefore, preRootAfter); // not required semantically, but ensures root file still maps

            var rootStats = plugin.GetDirStats(root);
            var subAStats = plugin.GetDirStats(subA);

            Assert.Equal(1, rootStats.FileCount);
            Assert.Equal(123L, rootStats.TotalBytes);

            Assert.Equal(0, subAStats.FileCount);
            Assert.Equal(0L, subAStats.TotalBytes);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RepoDirDeletedEvent_RemovesSubtree_Children_Ranges_And_FilePreorders()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var plugin = new TreeIndexPlugin(tempDir);

            // root(0)
            //   subA(1)
            //     subA1(2)
            //   subB(3)
            //
            // files:
            //   rootFile  under root
            //   subAFile  under subA
            //   subA1File under subA1
            //   subBFile  under subB
            var snap = RepoUtil.MakeSnapshotV2(
                scanRootId: 1,
                dirs:
                [
                    ("root", parentDirId: -1, dirId: 1),
                    ("subA", parentDirId: 1, dirId: 2),
                    ("subA1", parentDirId: 2, dirId: 4),
                    ("subB", parentDirId: 1, dirId: 3)
                ],
                files:
                [
                    ("rootFile.txt", dirId: 1, fileId: 10, size: 10L),
                    ("subAFile.txt", dirId: 2, fileId: 20, size: 20L),
                    ("subA1File.txt", dirId: 4, fileId: 30, size: 30L),
                    ("subBFile.txt", dirId: 3, fileId: 40, size: 40L)
                ]);

            plugin.Post(new BootstrapEvent { Generation = 1, RepoSnapshotView = snap });

            await plugin.WhenReadyAsync(TestContext.Current.CancellationToken);

            var root = new DirHandle(1, 0);
            var subA = new DirHandle(1, 1);
            var subA1 = new DirHandle(1, 2);
            var subB = new DirHandle(1, 3);

            var rootFile = new FileHandle(1, 0);
            var subAFile = new FileHandle(1, 1);
            var subA1File = new FileHandle(1, 2);
            var subBFile = new FileHandle(1, 3);

            Assert.Equal(RepoUtil.Sort([subA, subB]), RepoUtil.Sort(plugin.GetChildDirs(root).ToArray()));
            Assert.Equal(new[] { subA1 }, plugin.GetChildDirs(subA).ToArray());
            Assert.Equal(new[] { rootFile }, plugin.GetChildFiles(root).ToArray());
            Assert.Equal(new[] { subAFile }, plugin.GetChildFiles(subA).ToArray());
            Assert.Equal(new[] { subA1File }, plugin.GetChildFiles(subA1).ToArray());
            Assert.Equal(new[] { subBFile }, plugin.GetChildFiles(subB).ToArray());

            Assert.True(plugin.TryGetSubtreeRange(subA, out _));
            Assert.True(plugin.TryGetSubtreeRange(subA1, out _));
            Assert.True(plugin.TryGetFileDirPreorder(subAFile, out _));
            Assert.True(plugin.TryGetFileDirPreorder(subA1File, out _));
            Assert.True(plugin.TryGetFileDirPreorder(subBFile, out _));

            plugin.Post(new RepoDirDeletedEvent
            {
                Generation = 2,
                ScanRootId = 1,
                Dir = subA,
                DeletedDirIds = [2, 4],
                DeletedFiles =
                [
                    (20, subAFile),
                    (30, subA1File)
                ]
            });

            await AsyncUtil.WaitForConditionAsync(
                () =>
                    RepoUtil.Sort(plugin.GetChildDirs(root).ToArray()).SequenceEqual(RepoUtil.Sort([subB])) &&
                    plugin.GetChildDirs(subA).IsEmpty &&
                    plugin.GetChildFiles(subA).IsEmpty &&
                    plugin.GetChildFiles(subA1).IsEmpty,
                TimeSpan.FromSeconds(2));

            // root should now only point to subB
            Assert.Equal(new[] { subB }, plugin.GetChildDirs(root).ToArray());

            // Deleted subtree should be structurally disconnected
            Assert.True(plugin.GetChildDirs(subA).IsEmpty);
            Assert.True(plugin.GetChildFiles(subA).IsEmpty);
            Assert.True(plugin.GetChildFiles(subA1).IsEmpty);

            // Surviving side remains
            Assert.Equal(new[] { rootFile }, plugin.GetChildFiles(root).ToArray());
            Assert.Equal(new[] { subBFile }, plugin.GetChildFiles(subB).ToArray());

            // Deleted subtree ranges removed
            Assert.False(plugin.TryGetSubtreeRange(subA, out _));
            Assert.False(plugin.TryGetSubtreeRange(subA1, out _));

            // Surviving ranges still available
            Assert.True(plugin.TryGetSubtreeRange(root, out var rootRange));
            Assert.True(plugin.TryGetSubtreeRange(subB, out var subBRange));
            Assert.True(rootRange.Contains(subBRange.Start));

            // Deleted file preorder entries cleared; surviving ones remain
            Assert.False(plugin.TryGetFileDirPreorder(subAFile, out _));
            Assert.False(plugin.TryGetFileDirPreorder(subA1File, out _));
            Assert.True(plugin.TryGetFileDirPreorder(rootFile, out var rootPre));
            Assert.True(plugin.TryGetFileDirPreorder(subBFile, out var subBPre));
            Assert.True(rootRange.Contains(rootPre));
            Assert.True(rootRange.Contains(subBPre));

            var rootStats = plugin.GetDirStats(root);
            var subBStats = plugin.GetDirStats(subB);

            // Structural counts/bytes should drop by the deleted subtree contribution.
            Assert.Equal(1, rootStats.DirCount); // only subB remains beneath root
            Assert.Equal(2, rootStats.FileCount); // rootFile + subBFile
            Assert.Equal(50L, rootStats.TotalBytes); // 10 + 40

            Assert.Equal(0, subBStats.DirCount);
            Assert.Equal(1, subBStats.FileCount);
            Assert.Equal(40L, subBStats.TotalBytes);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task RepoDirDeletedEvent_DeletingRoot_RemovesRootRange_AndChildren()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var plugin = new TreeIndexPlugin(tempDir);

            var snap = RepoUtil.MakeSnapshotV2(
                scanRootId: 1,
                dirs:
                [
                    ("root", parentDirId: -1, dirId: 1),
                    ("child", parentDirId: 1, dirId: 2)
                ],
                files:
                [
                    ("rootFile.txt", dirId: 1, fileId: 10, size: 10L),
                    ("childFile.txt", dirId: 2, fileId: 20, size: 20L)
                ]);

            plugin.Post(new BootstrapEvent { Generation = 1, RepoSnapshotView = snap });

            await plugin.WhenReadyAsync(TestContext.Current.CancellationToken);

            var root = new DirHandle(1, 0);
            var child = new DirHandle(1, 1);
            var rootFile = new FileHandle(1, 0);
            var childFile = new FileHandle(1, 1);

            Assert.True(plugin.TryGetSubtreeRange(root, out _));
            Assert.Equal(new[] { child }, plugin.GetChildDirs(root).ToArray());

            plugin.Post(new RepoDirDeletedEvent
            {
                Generation = 2,
                ScanRootId = 1,
                Dir = root,
                DeletedDirIds = [1, 2],
                DeletedFiles =
                [
                    (10, rootFile),
                    (20, childFile)
                ]
            });

            await AsyncUtil.WaitForConditionAsync(
                () =>
                    plugin.GetChildDirs(root).IsEmpty &&
                    plugin.GetChildFiles(root).IsEmpty &&
                    !plugin.TryGetSubtreeRange(root, out _) &&
                    !plugin.TryGetFileDirPreorder(rootFile, out _) &&
                    !plugin.TryGetFileDirPreorder(childFile, out _),
                TimeSpan.FromSeconds(2));

            Assert.True(plugin.GetChildDirs(root).IsEmpty);
            Assert.True(plugin.GetChildFiles(root).IsEmpty);
            Assert.False(plugin.TryGetSubtreeRange(root, out _));
            Assert.False(plugin.TryGetSubtreeRange(child, out _));
            Assert.False(plugin.TryGetFileDirPreorder(rootFile, out _));
            Assert.False(plugin.TryGetFileDirPreorder(childFile, out _));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
