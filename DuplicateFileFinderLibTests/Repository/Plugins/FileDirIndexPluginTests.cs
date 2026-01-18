using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Plugins;
using DuplicateFileFinderLib.Repository.Storage.Models;

using DuplicateFileFinderLibTests.TestUtils;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Plugins;

public sealed class FileDirIndexPluginTests
{
    [Fact]
    public async Task BootstrapEvent_RebuildsIndex_FromSnapshot_AndResolvesHandles()
    {
        var dir = CreateTempDir();
        try
        {
            await using var plugin = new FileDirIndexPlugin(dir);

            var snapshot = MakeTwoRootSnapshot();

            plugin.Post(new BootstrapEvent
            {
                Generation = 1,
                RepoSnapshotView = snapshot
            });

            await plugin.WhenReadyAsync(CancellationToken.None);

            // Root 1:
            Assert.True(plugin.TryGetDir(101, out var d101));
            Assert.Equal(new DirHandle(1, 0), d101);

            Assert.True(plugin.TryGetDir(102, out var d102));
            Assert.Equal(new DirHandle(1, 1), d102);

            Assert.True(plugin.TryGetFile(1001, out var f1001));
            Assert.Equal(new FileHandle(1, 0), f1001);

            // Root 2:
            Assert.True(plugin.TryGetDir(201, out var d201));
            Assert.Equal(new DirHandle(2, 0), d201);

            Assert.True(plugin.TryGetFile(2001, out var f2001));
            Assert.Equal(new FileHandle(2, 0), f2001);

            // Not present
            Assert.False(plugin.TryGetDir(999, out _));
            Assert.False(plugin.TryGetFile(9999, out _));

            // State file created
            Assert.True(File.Exists(Path.Combine(dir, "file-dir-index.bin")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BootstrapEvent_LoadsPersistedState_WhenGenerationAndSequenceMatch()
    {
        var dir = CreateTempDir();
        try
        {
            // First instance builds and persists state
            await using (var plugin1 = new FileDirIndexPlugin(dir))
            {
                var snapshot1 = MakeTwoRootSnapshot();

                plugin1.Post(new BootstrapEvent
                {
                    Generation = 1,
                    RepoSnapshotView = snapshot1
                });

                await plugin1.WhenReadyAsync(TestContext.Current.CancellationToken);

                Assert.True(plugin1.TryGetDir(101, out var h));
                Assert.Equal(new DirHandle(1, 0), h);
            }

            // Second instance: give it a *different* snapshot, but same (gen, seq).
            // It should load the persisted index, not rebuild from the new snapshot.
            await using (var plugin2 = new FileDirIndexPlugin(dir))
            {
                var differentSnapshot = MakeDifferentSnapshotSameRoots();

                plugin2.Post(new BootstrapEvent
                {
                    Generation = 1,
                    RepoSnapshotView = differentSnapshot
                });

                await plugin2.WhenReadyAsync(TestContext.Current.CancellationToken);

                // These exist in the persisted state:
                Assert.True(plugin2.TryGetDir(101, out var d101));
                Assert.Equal(new DirHandle(1, 0), d101);

                // These exist only in the "differentSnapshot" and should NOT be present
                // if state was loaded rather than rebuilt.
                Assert.False(plugin2.TryGetDir(99901, out _));
                Assert.False(plugin2.TryGetFile(999001, out _));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BootstrapEvent_Rebuilds_WhenPersistedStateDoesNotMatchRepoPosition()
    {
        var dir = CreateTempDir();
        try
        {
            // Build persisted state at (gen=1, lastLogSeq=9)
            await using (var plugin1 = new FileDirIndexPlugin(dir))
            {
                plugin1.Post(new BootstrapEvent
                {
                    Generation = 1,
                    RepoSnapshotView = MakeTwoRootSnapshot()
                });
                await plugin1.WhenReadyAsync(CancellationToken.None);
            }

            // Now bootstrap with different repo position (gen or seq differs):
            // plugin must ignore state and rebuild from provided snapshot
            await using (var plugin2 = new FileDirIndexPlugin(dir))
            {
                var newSnapshot = MakeDifferentSnapshotSameRoots();

                plugin2.Post(new BootstrapEvent
                {
                    Generation = 2,        // changed
                    RepoSnapshotView = newSnapshot
                });

                await plugin2.WhenReadyAsync(CancellationToken.None);

                // Should reflect new snapshot content
                Assert.True(plugin2.TryGetDir(99901, out var d));
                Assert.Equal(new DirHandle(1, 0), d);

                Assert.True(plugin2.TryGetFile(999001, out var f));
                Assert.Equal(new FileHandle(1, 0), f);

                // And old IDs should be absent
                Assert.False(plugin2.TryGetDir(101, out _));
                Assert.False(plugin2.TryGetFile(1001, out _));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BootstrapEvent_RebuildsIndex_FromSnapshot_AndResolvesRelativePaths()
    {
        var dir = CreateTempDir();
        try
        {
            await using var plugin = new FileDirIndexPlugin(dir);

            var snapshot = MakeHierarchicalSnapshot();

            plugin.Post(new BootstrapEvent
            {
                Generation = 1,
                RepoSnapshotView = snapshot
            });

            await plugin.WhenReadyAsync(CancellationToken.None);

            // Handles resolve
            Assert.True(plugin.TryGetDir(101, out _));
            Assert.True(plugin.TryGetDir(102, out var d102));
            Assert.True(plugin.TryGetFile(1001, out var f1001));

            // Dir paths (relative to scan root)
            Assert.True(plugin.TryGetDirPathById(101, out var p101));
            Assert.Equal("a", p101);

            Assert.True(plugin.TryGetDirPathByHandle(d102, out var p102));
            Assert.Equal(Path.Combine("a", "b"), p102);

            // File paths (relative to scan root)
            Assert.True(plugin.TryGetFilePathByHandle(f1001, out var fp));
            Assert.Equal(Path.Combine("a", "b", "f.txt"), fp);

            Assert.True(plugin.TryGetFilePathById(1001, out var fp2));
            Assert.Equal(Path.Combine("a", "b", "f.txt"), fp2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PathResolution_ReturnsFalse_WhenSnapshotNotAvailable()
    {
        var dir = CreateTempDir();
        try
        {
            await using var plugin = new FileDirIndexPlugin(dir);

            // No bootstrap => no snapshot published; path resolution should fail.
            Assert.False(plugin.TryGetFilePathById(1001, out _));
            Assert.False(plugin.TryGetDirPathById(101, out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PathResolution_ReturnsFalse_ForUnknownIds()
    {
        var dir = CreateTempDir();
        try
        {
            await using var plugin = new FileDirIndexPlugin(dir);

            plugin.Post(new BootstrapEvent
            {
                Generation = 1,
                RepoSnapshotView = MakeHierarchicalSnapshot()
            });

            await plugin.WhenReadyAsync(CancellationToken.None);

            Assert.False(plugin.TryGetFilePathById(999999, out _));
            Assert.False(plugin.TryGetDirPathById(888888, out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ScanRootSnapshotCommittedEvent_RebuildsIndex_WhenGenerationIncreases()
    {
        var dir = CreateTempDir();
        try
        {
            await using var plugin = new FileDirIndexPlugin(dir);

            plugin.Post(new BootstrapEvent
            {
                Generation = 1,
                RepoSnapshotView = MakeTwoRootSnapshot()
            });

            await plugin.WhenReadyAsync(CancellationToken.None);

            Assert.True(plugin.TryGetDir(101, out _));
            Assert.False(plugin.TryGetDir(99901, out _));

            // Post committed event with gen=2 and different snapshot
            var newSnapshot = MakeDifferentSnapshotSameRoots();

            plugin.Post(new ScanRootSnapshotReplacedEvent
            {
                Generation = 2,
                ScanRootId = 1,
                RepoSnapshotView = newSnapshot,
                Reason = RepoSnapshotCommitReason.ScanCompleted
            });

            await AsyncUtil.WaitForConditionAsync(
                () => plugin.TryGetDir(99901, out _) && plugin.TryGetFile(999001, out _),
                TimeSpan.FromSeconds(2));

            Assert.False(plugin.TryGetDir(101, out _));
            Assert.False(plugin.TryGetFile(1001, out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------- Helpers ----------------

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FileDirIndexTests_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static RepoSnapshotView MakeTwoRootSnapshot()
    {
        // Deterministic ordering matters because handles are index-based.
        var snapshots = new Dictionary<long, ScanRootSnapshotView>
            {
                [1] = MakeRoot(
                    scanRootId: 1,
                    dirIds: [101L, 102L],
                    fileIds: [1001L]),
                [2] = MakeRoot(
                    scanRootId: 2,
                    dirIds: [201L],
                    fileIds: [2001L])
        };

        return new RepoSnapshotView
        {
            Snapshots = snapshots,
            ScanRoots = MakeScanRootsFromSnapshots(snapshots)
        };
    }

    private static RepoSnapshotView MakeDifferentSnapshotSameRoots()
    {
        var snapshots = new Dictionary<long, ScanRootSnapshotView>
            {
                [1] = MakeRoot(
                    scanRootId: 1,
                    dirIds: [99901L], // completely different IDs
                    fileIds: [999001L]),
                [2] = MakeRoot(
                    scanRootId: 2,
                    dirIds: [99902L],
                    fileIds: [999002L])
        };

        return new RepoSnapshotView
        {
            Snapshots = snapshots,
            ScanRoots = MakeScanRootsFromSnapshots(snapshots)
        };
    }

    private static RepoSnapshotView MakeHierarchicalSnapshot()
    {
        var snapshots = new Dictionary<long, ScanRootSnapshotView>
            {
                [1] = MakeHierarchicalRoot(scanRootId: 1)
        };

        return new RepoSnapshotView
        {
            Snapshots = snapshots,
            ScanRoots = MakeScanRootsFromSnapshots(snapshots)
        };
    }

    /// <summary>
    /// Builds a usable RepoSnapshotView.ScanRoots dictionary from the per-root snapshots.
    /// This is sufficient for plugin logic that needs to know which scan roots are live/deleted.
    /// </summary>
    private static Dictionary<long, ScanRoot> MakeScanRootsFromSnapshots(
        IReadOnlyDictionary<long, ScanRootSnapshotView> snapshots,
        Func<long, bool>? isDeleted = null,
        Func<long, long>? dirIdForRoot = null,
        Func<long, string>? rootPathForRoot = null,
        Func<long, string?>? volumePathForRoot = null,
        Func<long, string?>? volumeLabelForRoot = null,
        Func<long, string?>? displayNameForRoot = null)
    {
        var dict = new Dictionary<long, ScanRoot>(snapshots.Count);

        foreach (var (rootId, snapshot) in snapshots)
        {
            // Default: not deleted
            var deleted = isDeleted?.Invoke(rootId) ?? false;

            // Default: use first dir's DirId as the scan-root dirId if present
            var dirId = dirIdForRoot?.Invoke(rootId)
                        ?? (snapshot.Dirs.Count > 0 ? snapshot.Dirs[0].DirId : -1);

            var rootPath = rootPathForRoot?.Invoke(rootId) ?? $"root-{rootId}";
            var volPath = volumePathForRoot?.Invoke(rootId);
            var volLabel = volumeLabelForRoot?.Invoke(rootId);
            var displayName = displayNameForRoot?.Invoke(rootId);

            dict[rootId] = new ScanRoot
            {
                RootId = rootId,
                DirId = dirId,
                RootPath = rootPath,
                VolumePath = volPath,
                VolumeLabel = volLabel,
                DisplayName = displayName,
                IsDeleted = deleted,
                CreatedAt = default
            };
        }

        return dict;
    }

    private static ScanRootSnapshotView MakeRoot(long scanRootId, long[] dirIds, long[] fileIds)
    {
        // Minimal pool (indexing not used by FileDirIndexPlugin, but required by view).
        var pool = PackedStringPool.FromStrings(["x", ""]);

        var dirs = new DirRecordV2[dirIds.Length];
        for (int i = 0; i < dirIds.Length; i++)
        {
            dirs[i] = new DirRecordV2
            {
                DirId = dirIds[i],
                ParentDirId = -1,
                NameStrIdx = 0,
                ErrorMessageStrIdx = 1,
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                CreatedTicks = 0,
                ModifiedTicks = 0
            };
        }

        // Put all files under the first dir (if any)
        var parentDirId = dirIds.Length > 0 ? dirIds[0] : -1;

        var files = new FileRecordV2[fileIds.Length];
        for (int i = 0; i < fileIds.Length; i++)
        {
            files[i] = new FileRecordV2
            {
                FileId = fileIds[i],
                DirId = parentDirId,
                NameStrIdx = 0,
                ErrorMessageStrIdx = 1,
                Size = 1,
                Hash = HashKey.NotComputed,
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                CreatedTicks = 0,
                ModifiedTicks = 0
            };
        }

        return new ScanRootSnapshotView
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs = dirs,
            Files = files
        };
    }

    private static ScanRootSnapshotView MakeHierarchicalRoot(long scanRootId)
    {
        // pool indices: 0="a", 1="b", 2="f.txt", 3="" (error/empty)
        var pool = PackedStringPool.FromStrings(["a", "b", "f.txt", ""]);

        var dirs = new[]
        {
            new DirRecordV2
            {
                DirId = 101,
                ParentDirId = -1,
                NameStrIdx = 0,              // "a"
                ErrorMessageStrIdx = 3,
                Status = ScanEntryStatus.Enumerated,
                LastSeenScanSequence = 1,
                CreatedTicks = 0,
                ModifiedTicks = 0
            },
            new DirRecordV2
            {
                DirId = 102,
                ParentDirId = 101,
                NameStrIdx = 1,              // "b"
                ErrorMessageStrIdx = 3,
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
                FileId = 1001,
                DirId = 102,
                NameStrIdx = 2,              // "f.txt"
                ErrorMessageStrIdx = 3,
                Size = 1,
                Hash = HashKey.NotComputed,
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
