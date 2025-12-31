using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Plugins;
using DuplicateFileFinderLib.Repository.Plugins.Models;

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
                    ("root", parentDirId: -1L, dirId: 1L),
                    ("subA", parentDirId: 1L, dirId: 2L),
                    ("subB", parentDirId: 1L, dirId: 3L)
                ],
                files:
                [
                    ("file_root.txt", dirId: 1L, fileId: 10L, size: 123L),
                    ("file_subA.txt", dirId: 2L, fileId: 20L, size: 456L)
                ]);

            plugin.Post(new BootstrapEvent
            {
                Generation = 1,
                RepoSnapshotView = snap
            });

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
            Assert.Empty(plugin.GetChildDirs(subA));
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
                        ("root", parentDirId: -1L, dirId: 1L),
                        ("subA", parentDirId: 1L, dirId: 2L)
                    ],
                    files:
                    [
                        ("file_subA.txt", dirId: 1L, fileId: 10L, size: 1L)
                    ]);

                plugin1.Post(new BootstrapEvent
                {
                    Generation = 1,
                    RepoSnapshotView = snap1
                });

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
                        ("root", parentDirId: -1L, dirId: 1L),
                        ("subB", parentDirId: 1L, dirId: 3L)
                    ],
                    files:
                    [
                        ("fileB.txt", dirId: 1L, fileId: 99L, size: 2L)
                    ]);

                plugin2.Post(new BootstrapEvent
                {
                    Generation = 1,
                    RepoSnapshotView = snap2
                });

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
                    ("root", parentDirId: -1L, dirId: 1L),
                    ("subA", parentDirId: 1L, dirId: 2L)
                ],
                files: Array.Empty<(string name, long dirId, long fileId, long size)>());

            plugin.Post(new BootstrapEvent { Generation = 1, RepoSnapshotView = snap1 });
            await plugin.WhenReadyAsync(CancellationToken.None);

            var rootHandle = new DirHandle(1, 0);
            Assert.Single(plugin.GetChildDirs(rootHandle));

            // Snapshot 2: replace subA with subB (still child at index 1 but different DirId behind it)
            var snap2 = RepoUtil.MakeSnapshotV2(
                scanRootId: 1,
                dirs:
                [
                    ("root", parentDirId: -1L, dirId: 1L),
                    ("subB", parentDirId: 1L, dirId: 3L)
                ],
                files: Array.Empty<(string name, long dirId, long fileId, long size)>());

            plugin.Post(new ScanRootSnapshotCommittedEvent
            {
                Generation = 2,
                ScanRootId = 1,
                RepoSnapshotView = snap2
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
}
