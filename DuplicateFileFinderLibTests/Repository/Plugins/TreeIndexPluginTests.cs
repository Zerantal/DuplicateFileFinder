using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Plugins
{
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

                // Layout:
                //   Dir 1: root (no parent)
                //     Dir 2: subA
                //     Dir 3: subB
                //     File 10: file_root.txt
                //   Dir 2:
                //     File 20: file_subA.txt
                var now = DateTime.UtcNow;

                var dirs = new Dictionary<long, DirRecord>
                {
                    [1] = new DirRecord
                    {
                        DirId = 1,
                        ParentDirId = null,
                        Name = "root",
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated
                    },
                    [2] = new DirRecord
                    {
                        DirId = 2,
                        ParentDirId = 1,
                        Name = "subA",
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated
                    },
                    [3] = new DirRecord
                    {
                        DirId = 3,
                        ParentDirId = 1,
                        Name = "subB",
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated
                    }
                };

                var files = new Dictionary<long, FileRecord>
                {
                    [10] = new FileRecord
                    {
                        FileId = 10,
                        DirId = 1,
                        Name = "file_root.txt",
                        Size = 123,
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated,
                        Hash = HashKey.NotComputed
                    },
                    [20] = new FileRecord
                    {
                        FileId = 20,
                        DirId = 2,
                        Name = "file_subA.txt",
                        Size = 456,
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated,
                        Hash = HashKey.NotComputed
                    }
                };

                var snapshot = new RepoView(dirs, files);

                var bootstrap = new BootstrapEvent
                {
                    Generation = 1,
                    NextLogSequence = 10,
                    Snapshot = snapshot
                };

                plugin.Post(bootstrap);
                await plugin.WhenReadyAsync(CancellationToken.None);

                var rootChildDirs = plugin.GetChildDirIds(1).OrderBy(x => x).ToArray();
                var rootChildFiles = plugin.GetChildFileIds(1).OrderBy(x => x).ToArray();

                Assert.Equal(new[] { 2L, 3L }, rootChildDirs);
                Assert.Equal(new[] { 10L }, rootChildFiles);

                var subAChildDirs = plugin.GetChildDirIds(2);
                var subAChildFiles = plugin.GetChildFileIds(2).OrderBy(x => x).ToArray();

                Assert.Empty(subAChildDirs);
                Assert.Equal(new[] { 20L }, subAChildFiles);

                var statePath = Path.Combine(tempDir, "tree-index.bin");
                Assert.True(File.Exists(statePath));
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task BootstrapEvent_LoadsExistingState_WhenStateMatchesGenerationAndSequence()
        {
            var tempDir = CreateTempDir();
            try
            {
                var now = DateTime.UtcNow;

                // First run: build state with one structure.
                await using (var plugin1 = new TreeIndexPlugin(tempDir))
                {
                    var dirs1 = new Dictionary<long, DirRecord>
                    {
                        [1] = new DirRecord
                        {
                            DirId = 1,
                            ParentDirId = null,
                            Name = "root",
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated
                        },
                        [2] = new DirRecord
                        {
                            DirId = 2,
                            ParentDirId = 1,
                            Name = "subA",
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated
                        }
                    };

                    var files1 = new Dictionary<long, FileRecord>
                    {
                        [10] = new FileRecord
                        {
                            FileId = 10,
                            DirId = 1,
                            Name = "fileA.txt",
                            Size = 1,
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated,
                            Hash = HashKey.NotComputed
                        }
                    };

                    var snapshot1 = new RepoView(dirs1, files1);

                    var bootstrap1 = new BootstrapEvent
                    {
                        Generation = 1,
                        NextLogSequence = 10,
                        Snapshot = snapshot1
                    };

                    plugin1.Post(bootstrap1);
                    await plugin1.WhenReadyAsync(TestContext.Current.CancellationToken);

                    var rootChildDirs1 = plugin1.GetChildDirIds(1).ToArray();
                    var rootChildFiles1 = plugin1.GetChildFileIds(1).ToArray();

                    Assert.Equal(new[] { 2L }, rootChildDirs1);
                    Assert.Equal(new[] { 10L }, rootChildFiles1);
                }

                // Second run: use a different snapshot, but same (Generation, NextLogSequence).
                // TreeIndexPlugin should ignore the snapshot and load persisted state instead.
                await using (var plugin2 = new TreeIndexPlugin(tempDir))
                {
                    var dirs2 = new Dictionary<long, DirRecord>
                    {
                        [1] = new DirRecord
                        {
                            DirId = 1,
                            ParentDirId = null,
                            Name = "root",
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated
                        },
                        [3] = new DirRecord
                        {
                            DirId = 3,
                            ParentDirId = 1,
                            Name = "subB",
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated
                        }
                    };

                    var files2 = new Dictionary<long, FileRecord>
                    {
                        [99] = new FileRecord
                        {
                            FileId = 99,
                            DirId = 1,
                            Name = "fileB.txt",
                            Size = 2,
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated,
                            Hash = HashKey.NotComputed
                        }
                    };

                    var snapshot2 = new RepoView(dirs2, files2);

                    var bootstrap2 = new BootstrapEvent
                    {
                        Generation = 1,
                        NextLogSequence = 10, // same as first run
                        Snapshot = snapshot2
                    };

                    plugin2.Post(bootstrap2);
                    await plugin2.WhenReadyAsync(TestContext.Current.CancellationToken);

                    var rootChildDirs2 = plugin2.GetChildDirIds(1).OrderBy(x => x).ToArray();
                    var rootChildFiles2 = plugin2.GetChildFileIds(1).OrderBy(x => x).ToArray();

                    // Should reflect persisted state (dir 2, file 10), not new snapshot (dir 3, file 99)
                    Assert.Equal(new[] { 2L }, rootChildDirs2);
                    Assert.Equal(new[] { 10L }, rootChildFiles2);
                }
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task CompactedEvent_RebuildsIndexAndPersistsNewState()
        {
            var tempDir = CreateTempDir();
            try
            {
                var now = DateTime.UtcNow;

                // Initial snapshot
                await using (var plugin = new TreeIndexPlugin(tempDir))
                {
                    var dirs1 = new Dictionary<long, DirRecord>
                    {
                        [1] = new DirRecord
                        {
                            DirId = 1,
                            ParentDirId = null,
                            Name = "root",
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated
                        },
                        [2] = new DirRecord
                        {
                            DirId = 2,
                            ParentDirId = 1,
                            Name = "oldSub",
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated
                        }
                    };

                    var files1 = new Dictionary<long, FileRecord>
                    {
                        [10] = new FileRecord
                        {
                            FileId = 10,
                            DirId = 1,
                            Name = "oldFile.txt",
                            Size = 1,
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated,
                            Hash = HashKey.NotComputed
                        }
                    };

                    var snapshot1 = new RepoView(dirs1, files1);

                    var bootstrap = new BootstrapEvent
                    {
                        Generation = 1,
                        NextLogSequence = 10,
                        Snapshot = snapshot1
                    };

                    plugin.Post(bootstrap);
                    await plugin.WhenReadyAsync(TestContext.Current.CancellationToken);

                    Assert.Equal([2L], plugin.GetChildDirIds(1));
                    Assert.Equal([10L], plugin.GetChildFileIds(1));

                    // New snapshot after compaction
                    var dirs2 = new Dictionary<long, DirRecord>
                    {
                        [1] = new DirRecord
                        {
                            DirId = 1,
                            ParentDirId = null,
                            Name = "root",
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated
                        },
                        [3] = new DirRecord
                        {
                            DirId = 3,
                            ParentDirId = 1,
                            Name = "newSub",
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated
                        }
                    };

                    var files2 = new Dictionary<long, FileRecord>
                    {
                        [20] = new FileRecord
                        {
                            FileId = 20,
                            DirId = 1,
                            Name = "newFile.txt",
                            Size = 2,
                            Created = now,
                            Modified = now,
                            Status = ScanEntryStatus.Enumerated,
                            Hash = HashKey.NotComputed
                        }
                    };

                    var snapshot2 = new RepoView(dirs2, files2);

                    var compacted = new CompactedEvent
                    {
                        Generation = 2,
                        NextLogSequence = 20,
                        Snapshot = snapshot2
                    };

                    plugin.Post(compacted);

                    // ChannelRepoPlugin handles events asynchronously; give it a little time.
                    await Task.Delay(20, TestContext.Current.CancellationToken);

                    var rootChildDirs = plugin.GetChildDirIds(1).ToArray();
                    var rootChildFiles = plugin.GetChildFileIds(1).ToArray();

                    Assert.Equal(new[] { 3L }, rootChildDirs);
                    Assert.Equal(new[] { 20L }, rootChildFiles);
                }

                // New plugin instance should pick up the state written by CompactedEvent
                await using (var plugin2 = new TreeIndexPlugin(tempDir))
                {
                    var emptySnapshot = new RepoView(
                        new Dictionary<long, DirRecord>(),
                        new Dictionary<long, FileRecord>());

                    var bootstrap2 = new BootstrapEvent
                    {
                        Generation = 2,
                        NextLogSequence = 20,
                        Snapshot = emptySnapshot
                    };

                    plugin2.Post(bootstrap2);
                    await plugin2.WhenReadyAsync(TestContext.Current.CancellationToken);

                    var rootChildDirs2 = plugin2.GetChildDirIds(1).ToArray();
                    var rootChildFiles2 = plugin2.GetChildFileIds(1).ToArray();

                    Assert.Equal(new[] { 3L }, rootChildDirs2);
                    Assert.Equal(new[] { 20L }, rootChildFiles2);
                }
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task DeltaCommittedEvent_DoesNotChangeIndex_WhenNotHandled()
        {
            var tempDir = CreateTempDir();
            try
            {
                await using var plugin = new TreeIndexPlugin(tempDir);

                var now = DateTime.UtcNow;

                var dirs = new Dictionary<long, DirRecord>
                {
                    [1] = new DirRecord
                    {
                        DirId = 1,
                        ParentDirId = null,
                        Name = "root",
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated
                    }
                };

                var files = new Dictionary<long, FileRecord>
                {
                    [10] = new FileRecord
                    {
                        FileId = 10,
                        DirId = 1,
                        Name = "file.txt",
                        Size = 1,
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated,
                        Hash = HashKey.NotComputed
                    }
                };

                var snapshot = new RepoView(dirs, files);

                var bootstrap = new BootstrapEvent
                {
                    Generation = 1,
                    NextLogSequence = 5,
                    Snapshot = snapshot
                };

                plugin.Post(bootstrap);
                await plugin.WhenReadyAsync(TestContext.Current.CancellationToken);

                var originalChildDirs = plugin.GetChildDirIds(1).ToArray();
                var originalChildFiles = plugin.GetChildFileIds(1).ToArray();

                // TreeIndexPlugin currently only handles BootstrapEvent and CompactedEvent.
                // DeltaCommittedEvent should be ignored by the plugin.
                var delta = new RepoDelta
                {
                    ScanSequence = 1,
                    Files = [],
                    Dirs = [],
                };

                var deltaEvent = new DeltaCommittedEvent
                {
                    Generation = 1,
                    NextLogSequence = 6,
                    ScanSequence = 1,
                    Delta = delta
                };

                plugin.Post(deltaEvent);
                await Task.Delay(20, TestContext.Current.CancellationToken);

                Assert.Equal(originalChildDirs, plugin.GetChildDirIds(1).ToArray());
                Assert.Equal(originalChildFiles, plugin.GetChildFileIds(1).ToArray());
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}