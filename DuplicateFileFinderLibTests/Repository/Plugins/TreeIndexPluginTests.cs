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
            var now = DateTimeOffset.UtcNow;

            // IMPORTANT: arrays are in deterministic order so indices are deterministic
            var dirs = new[]
            {
                new DirRecord
                {
                    DirId = 1, ParentDirId = null, Name = "root", Status = ScanEntryStatus.Enumerated, Created = now,
                    Modified = now
                },
                new DirRecord
                {
                    DirId = 2, ParentDirId = 1, Name = "subA", Status = ScanEntryStatus.Enumerated, Created = now,
                    Modified = now
                },
                new DirRecord
                {
                    DirId = 3, ParentDirId = 1, Name = "subB", Status = ScanEntryStatus.Enumerated, Created = now,
                    Modified = now
                }
            };

            var files = new[]
            {
                new FileRecord
                {
                    FileId = 10, DirId = 1, Name = "file_root.txt", Size = 123, Status = ScanEntryStatus.Enumerated,
                    Hash = HashKey.NotComputed, Created = now, Modified = now
                },
                new FileRecord
                {
                    FileId = 20, DirId = 2, Name = "file_subA.txt", Size = 456, Status = ScanEntryStatus.Enumerated,
                    Hash = HashKey.NotComputed, Created = now, Modified = now
                }
            };

            var snapshot = RepoUtil.MakeSnapshot(1, dirs, files);

            plugin.Post(new BootstrapEvent
            {
                Generation = 1,
                NextLogSequence = 10,
                RepoSnapshotView = snapshot
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
            var now = DateTimeOffset.UtcNow;

            // First run: build state.
            await using (var plugin1 = new TreeIndexPlugin(tempDir))
            {
                var dirs1 = new[]
                {
                    new DirRecord
                    {
                        DirId = 1,
                        ParentDirId = null,
                        Name = "root",
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated
                    },
                    new DirRecord
                    {
                        DirId = 2,
                        ParentDirId = 1,
                        Name = "subA",
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated
                    }
                };

                var files1 = new[]
                {
                    new FileRecord
                    {
                        FileId = 10,
                        DirId = 1,
                        Name = "file_subA.txt",
                        Size = 1,
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated,
                        Hash = HashKey.NotComputed
                    }
                };

                var snapshot = RepoUtil.MakeSnapshot(scanRootId: 1, dirs1, files1);

                var bootstrap1 = new BootstrapEvent
                {
                    Generation = 1,
                    NextLogSequence = 10,
                    RepoSnapshotView = snapshot
                };

                plugin1.Post(bootstrap1);
                await plugin1.WhenReadyAsync(TestContext.Current.CancellationToken);
                
                var root = new DirHandle(1, 0);
                var subA = new DirHandle(1, 1);
                var fileSubA = new FileHandle(1, 0);

                Assert.Equal(new[] { subA }, plugin1.GetChildDirs(root).ToArray());
                Assert.Equal(new[] { fileSubA }, plugin1.GetChildFiles(root).ToArray());
            }

            // Second run: different snapshot, same (Generation, NextLogSequence) -> should load persisted state.
            await using (var plugin2 = new TreeIndexPlugin(tempDir))
            {
                var dirs2 = new[]
                {
                    new  DirRecord
                    {
                        DirId = 1,
                        ParentDirId = null,
                        Name = "root",
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated
                    },
                    new DirRecord
                    {
                        DirId = 3,
                        ParentDirId = 1,
                        Name = "subB",
                        Created = now,
                        Modified = now,
                        Status = ScanEntryStatus.Enumerated
                    }
                };

                var files2 = new[]
                {
                    new FileRecord
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

                var snapshot = RepoUtil.MakeSnapshot(1, dirs2, files2);

                var bootstrap2 = new BootstrapEvent
                {
                    Generation = 1,
                    NextLogSequence = 10,
                    RepoSnapshotView = snapshot
                };

                plugin2.Post(bootstrap2);
                await plugin2.WhenReadyAsync(TestContext.Current.CancellationToken);

                var root = new DirHandle(1, 0);
                var subB = new DirHandle(1, 1);
                var fileSubB = new FileHandle(1, 0);
                
                var rootChildDirs2 = plugin2.GetChildDirs(root).ToArray();
                var rootChildFiles2 = plugin2.GetChildFiles(root).ToArray();

                // Should reflect persisted state (dir 2, file 10), not new snapshot (dir 3, file 99)
                Assert.Equal(new[] { subB }, rootChildDirs2);
                Assert.Equal(new[] { fileSubB }, rootChildFiles2);
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CompactedEvent_RebuildsIndexAndPersistsNewState()
    {
        var tempDir = CreateTempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
    
            // Initial snapshot
            await using var plugin = new TreeIndexPlugin(tempDir);
            
            var dirs1 = new[]
            {
                new DirRecord
                {
                    DirId = 1,
                    ParentDirId = null,
                    Name = "root",
                    Created = now,
                    Modified = now,
                    Status = ScanEntryStatus.Enumerated
                },
                new DirRecord
                {
                    DirId = 2,
                    ParentDirId = 1,
                    Name = "oldSub",
                    Created = now,
                    Modified = now,
                    Status = ScanEntryStatus.Enumerated
                }
            };
    
            var files1 = new FileRecord[]
            {
                new()
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
    
            var snapshot1 = RepoUtil.MakeSnapshot(1, dirs1, files1);
    
            var bootstrap = new BootstrapEvent
            {
                Generation = 1,
                NextLogSequence = 10,
                RepoSnapshotView = snapshot1
            };
    
            plugin.Post(bootstrap);
            await plugin.WhenReadyAsync(TestContext.Current.CancellationToken);
    
            var root = new DirHandle(1, 0);
            var oldSubB = new DirHandle(1, 1);
            var oldFile = new FileHandle(1, 0);
                
            Assert.Equal(new[] { oldSubB }, plugin.GetChildDirs(root));
            Assert.Equal(new[] { oldFile }, plugin.GetChildFiles(root));
    
            // New snapshot after compaction
            var dirs2 = new DirRecord[]
            {
                new()
                {
                    DirId = 1,
                    ParentDirId = null,
                    Name = "root",
                    Created = now,
                    Modified = now,
                    Status = ScanEntryStatus.Enumerated
                },
                new()
                {
                    DirId = 3,
                    ParentDirId = 1,
                    Name = "newSub",
                    Created = now,
                    Modified = now,
                    Status = ScanEntryStatus.Enumerated
                }
            };
    
            var files2 = new FileRecord[]
            {
                new()
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
    
            var snapshot2 = RepoUtil.MakeSnapshot(1, dirs2, files2);
    
            var compacted = new CompactedEvent
            {
                Generation = 2,
                NextLogSequence = 999,
                RepoSnapshotView = snapshot2
            };
    
            plugin.Post(compacted);
            await plugin.WhenReadyAsync(TestContext.Current.CancellationToken);
    
            var newSubB = new DirHandle(1, 1);
            var newFile = new FileHandle(1, 0);
                
            var rootChildDirs2 = plugin.GetChildDirs(root).ToArray();
            var rootChildFiles2 = plugin.GetChildFiles(root).ToArray();
    
            Assert.Equal(new[] { newSubB }, rootChildDirs2);
            Assert.Equal(new[] { newFile }, rootChildFiles2);
    
            var statePath = Path.Combine(tempDir, "tree-index.bin");
            Assert.True(File.Exists(statePath));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DeltaCommittedEvent_DoesNotChangeIndex_WhenNotHandled()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var plugin = new TreeIndexPlugin(tempDir);
    
            var now = DateTimeOffset.UtcNow;
    
            var dirs = new DirRecord[]
            {
                new()
                {
                    DirId = 1,
                    ParentDirId = null,
                    Name = "root",
                    Created = now,
                    Modified = now,
                    Status = ScanEntryStatus.Enumerated
                }
            };
    
            var files = new FileRecord[]
            {
                new()
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
    
            var snapshot = RepoUtil.MakeSnapshot(1, dirs, files);
    
            var bootstrap = new BootstrapEvent
            {
                Generation = 1,
                NextLogSequence = 5,
                RepoSnapshotView = snapshot
            };
    
            plugin.Post(bootstrap);
            await plugin.WhenReadyAsync(TestContext.Current.CancellationToken);
    
            var root =  new DirHandle(1, 0);
            
            var originalChildDirs = plugin.GetChildDirs(root).ToArray();
            var originalChildFiles = plugin.GetChildFiles(root).ToArray();
    
            // TreeIndexPlugin currently only handles BootstrapEvent and CompactedEvent.
            // DeltaCommittedEvent should be ignored by the plugin.
            var delta = new RepoDelta
            {
                ScanSequence = 1,
                Files = [],
                Dirs = []
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
            
            Assert.Equal(originalChildDirs, plugin.GetChildDirs(root).ToArray());
            Assert.Equal(originalChildFiles, plugin.GetChildFiles(root).ToArray());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}