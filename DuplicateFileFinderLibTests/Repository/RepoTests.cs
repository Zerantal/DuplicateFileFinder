using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository
{
    public sealed class RepoTests : IDisposable
    {
        private readonly string _rootDir;

        public RepoTests()
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "dff-repo-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_rootDir))
                    Directory.Delete(_rootDir, recursive: true);
            }
            catch
            {
                // ignore cleanup errors
            }
        }

        private string MetaPath   => Path.Combine(_rootDir, "repo.mp");
        private string LogDir => Path.Combine(_rootDir, "log");

        private RepoMeta ReadMeta()
        {
            var bytes = File.ReadAllBytes(MetaPath);
            var metaFile = MemoryPackSerializer.Deserialize<RepoMetaFile>(bytes)!;
            return metaFile.Meta;
        }

        [Fact]
        public async Task Open_NewRepo_CreatesMetaAndLogDirectory()
        {
            Assert.False(File.Exists(MetaPath));
            Assert.False(Directory.Exists(LogDir));

            await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(MetaPath));
            Assert.True(Directory.Exists(LogDir));

            var meta = ReadMeta();
            Assert.Equal(5, meta.SchemaVersion);
            Assert.Equal(1, meta.Generation);
            Assert.Equal(0, meta.NextLogSequence);
            Assert.Equal(-1, meta.LastSnapshottedLogSequence);
            Assert.NotEqual(Guid.Empty, meta.RepoId);
            Assert.Equal(_rootDir, meta.RepoPath);
            Assert.False(string.IsNullOrWhiteSpace(meta.RepoHostName));
            Assert.Equal(0, meta.NextScanSequence);
        }

        [Fact]
        public async Task AllocateScanSequence_UsesAndPersistsNextScanSequence()
        {
            var repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);
            var metaBefore = ReadMeta();
            Assert.Equal(0, metaBefore.NextScanSequence);

            var seq1 = repo.AllocateScanSequence();
            var metaAfter1 = ReadMeta();

            // EXPECTED semantics:
            // - allocate current value
            // - persist +1
            Assert.Equal(0, seq1);
            Assert.Equal(1, metaAfter1.NextScanSequence);

            var seq2 = repo.AllocateScanSequence();
            var metaAfter2 = ReadMeta();

            Assert.Equal(1, seq2);
            Assert.Equal(2, metaAfter2.NextScanSequence);
        }

        [Fact]
        public async Task CommitDelta_WritesDeltaFileAndAdvancesNextLogSequence()
        {
            var repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);
            var metaBefore = ReadMeta();
            Assert.Equal(0, metaBefore.NextLogSequence);

            var delta = new RepoDelta
            {
                ScanSequence = 0,
                Files = [],
                Dirs = [],
                DeletedFiles = [],
                DeletedDirs = []
            };

            await repo.CommitDeltaAsync(delta, TestContext.Current.CancellationToken);

            var metaAfter = ReadMeta();
            Assert.Equal(1, metaAfter.NextLogSequence);

            var deltaFiles = Directory.GetFiles(LogDir, $"{metaAfter.Generation}-*.delta");
            Assert.Single(deltaFiles);
        }

        [Fact]
        public async Task CommitDelta_WithDir_AllowsGetFullDirPathOnReopen()
        {
            var repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);

            var rootDirId = Guid.NewGuid();
            var rootDirRecord = new DirRecord
            {
                DirId = rootDirId,
                ParentId = null,
                Name = "root",
                LastSeenSequence = 0,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            var delta = new RepoDelta
            {
                ScanSequence = 0,
                Dirs = [rootDirRecord]
            };

            await repo.CommitDeltaAsync(delta, TestContext.Current.CancellationToken);

            // Close and reopen to force replay from log
            repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);

            var fullPath = repo.GetFullDirPath(rootDirId);
            Assert.Equal("/root", fullPath);
        }

        [Fact]
        public async Task CommitDelta_WithFile_UpdatesHashIndex()
        {
            var repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);

            var dirId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            var dir = new DirRecord
            {
                DirId = dirId,
                ParentId = null,
                Name = "root",
                LastSeenSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            var hashBytes = new byte[16];
            new Random(123).NextBytes(hashBytes);
            var hashKey = new HashKey(hashBytes);

            var file = new FileRecord
            {
                FileId = fileId,
                DirId = dirId,
                Name = "file.txt",
                Size = 100,
                Hash = hashKey,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Hashed,
                ErrorMessage = null
            };

            var delta = new RepoDelta
            {
                ScanSequence = 1,
                Files = [file],
                Dirs = [dir]
            };

            await repo.CommitDeltaAsync(delta, TestContext.Current.CancellationToken);

            var snapshot = repo.GetSnapshot();
            Assert.Single(snapshot.Dirs);
            Assert.Single(snapshot.Files);
            Assert.Single(snapshot.HashIndex);

            var hashEntry = Assert.Single(snapshot.HashIndex);
            Assert.Equal(hashKey, hashEntry.Key);
            Assert.Contains(fileId, hashEntry.Value);
        }

        [Fact]
        public async Task ApplyDelta_WithTombstones_RemovesFilesAndHashIndex()
        {
            var repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);

            var dirId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            var dir = new DirRecord
            {
                DirId = dirId,
                ParentId = null,
                Name = "root",
                LastSeenSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            var hashBytes = new byte[16];
            new Random(456).NextBytes(hashBytes);
            var hashKey = new HashKey(hashBytes);

            var file = new FileRecord
            {
                FileId = fileId,
                DirId = dirId,
                Name = "file.txt",
                Size = 123,
                Hash = hashKey,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            // First delta: add dir+file
            await repo.CommitDeltaAsync(new RepoDelta
            {
                ScanSequence = 1,
                Dirs = [dir],
                Files = [file]
            }, TestContext.Current.CancellationToken);

            var snapshot1 = repo.GetSnapshot();
            Assert.Single(snapshot1.Files);
            Assert.Single(snapshot1.HashIndex);

            // Second delta: delete file
            await repo.CommitDeltaAsync(new RepoDelta
            {
                ScanSequence = 2,
                DeletedFiles = [new(fileId, 2)]
            }, TestContext.Current.CancellationToken);

            var snapshot2 = repo.GetSnapshot();
            Assert.Empty(snapshot2.Files);
            Assert.True(snapshot2.HashIndex.Count == 0 ||
                        !snapshot2.HashIndex.Values.SelectMany(x => x).Contains(fileId));
        }

        [Fact]
        public async Task SaveSnapshot_And_ReplayDeltas_RestoreState()
        {
            IRepo repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);

            var dirId = Guid.NewGuid();
            var fileId1 = Guid.NewGuid();
            var fileId2 = Guid.NewGuid();

            var hashBytes1 = new byte[16];
            new Random(1).NextBytes(hashBytes1);
            var hash1 = new HashKey(hashBytes1);

            var hashBytes2 = new byte[16];
            new Random(2).NextBytes(hashBytes2);
            var hash2 = new HashKey(hashBytes2);

            var seq = (repo as Repo)!.AllocateScanSequence();
            
            var dir = new DirRecord
            {
                DirId = dirId,
                ParentId = null,
                Name = "root",
                LastSeenSequence = seq,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            var file1 = new FileRecord
            {
                FileId = fileId1,
                DirId = dirId,
                Name = "f1",
                Size = 10,
                Hash = hash1,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = seq,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            // Delta 1: dir + file1
            await repo.CommitDeltaAsync(new RepoDelta
            {
                ScanSequence = seq,
                Dirs = [dir],
                Files = [file1]
            }, TestContext.Current.CancellationToken);

            // Ensure there is a ScanRoot for this logical path so SaveScanSnapshots
            // actually writes a per-root snapshot containing dir + file1.
            var rootPath = "/root";
            _ = repo.BeginScan(rootPath);

            repo.SaveScanSnapshots(); // snapshot baseline (captures dir + file1)

            // Delta 2: add file2 after snapshot
            seq = (repo as Repo)!.AllocateScanSequence();
            var file2 = new FileRecord
            {
                FileId = fileId2,
                DirId = dirId,
                Name = "f2",
                Size = 20,
                Hash = hash2,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = seq,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };
            
            await repo.CommitDeltaAsync(new RepoDelta
            {
                ScanSequence = seq,
                Files = [file2]
            }, TestContext.Current.CancellationToken);
            
            // Reopen: should load baseline + replay delta2
            repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);

            var snapshot = repo.GetSnapshot();

            Assert.Equal(2, snapshot.Files.Count);
            Assert.True(snapshot.Files.ContainsKey(fileId1));
            Assert.True(snapshot.Files.ContainsKey(fileId2));
        }

        [Fact]
        public async Task CompactNow_DeletesAllCoveredDeltaFiles()
        {
            var repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);

            // Write a couple of deltas
            await repo.CommitDeltaAsync(new RepoDelta { ScanSequence = 0 }, TestContext.Current.CancellationToken);
            await repo.CommitDeltaAsync(new RepoDelta { ScanSequence = 1 }, TestContext.Current.CancellationToken);

            var metaBefore = ReadMeta();
            Assert.Equal(2, metaBefore.NextLogSequence); // ids 0,1 allocated

            await repo.CompactAsync(ct: TestContext.Current.CancellationToken);

            var metaAfter = ReadMeta();

            // New invariant: snapshot covers 0..(NextLogSequence-1)
            Assert.Equal(metaAfter.NextLogSequence - 1, metaAfter.LastSnapshottedLogSequence);

            var deltaFilesAfter = Directory.GetFiles(LogDir, $"{metaAfter.Generation}-*.delta");
            Assert.Empty(deltaFilesAfter);
        }

        [Fact]
        public async Task GetFullDirPath_ReconstructsHierarchy()
        {
            var repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);

            var rootId = Guid.NewGuid();
            var childId = Guid.NewGuid();

            var rootDir = new DirRecord
            {
                DirId = rootId,
                ParentId = null,
                Name = "root",
                LastSeenSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            var childDir = new DirRecord
            {
                DirId = childId,
                ParentId = rootId,
                Name = "child",
                LastSeenSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            await repo.CommitDeltaAsync(new RepoDelta
            {
                ScanSequence = 1,
                Dirs = [rootDir, childDir]
            }, TestContext.Current.CancellationToken);

            var pathRoot = repo.GetFullDirPath(rootId);
            var pathChild = repo.GetFullDirPath(childId);

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal("root", pathRoot);
                Assert.Equal(Path.Combine("root", "child"), pathChild);
            }
            else
            {
                Assert.Equal("/root", pathRoot);
                Assert.Equal("/root/child", pathChild.Replace('\\', '/'));
            }

            // Ensure cache hit path also works
            var pathChild2 = repo.GetFullDirPath(childId);
            Assert.Equal(pathChild, pathChild2);
        }

        // NOTE: This test requires InternalsVisibleTo("DuplicateFileFinderLib.Tests")
        // on the main assembly to access CompleteScanForRoot.
        [Fact]
        public async Task CompleteScanForRoot_ProducesTombstonesForMissingFiles()
        {
            var repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);

            var rootId    = Guid.NewGuid();
            var subId     = Guid.NewGuid();
            var fileOldId = Guid.NewGuid();
            var fileNewId = Guid.NewGuid();

            var rootDir = new DirRecord
            {
                DirId               = rootId,
                ParentId         = null,
                Name             = "root",
                LastSeenSequence = 1,
                Status           = ScanEntryStatus.Enumerated,
                ErrorMessage     = null
            };

            var subDir = new DirRecord
            {
                DirId               = subId,
                ParentId         = rootId,
                Name             = "sub",
                LastSeenSequence = 1,
                Status           = ScanEntryStatus.Enumerated,
                ErrorMessage     = null
            };

            var hashBytes = new byte[16];
            new Random(111).NextBytes(hashBytes);
            var hash = new HashKey(hashBytes);

            var oldFile = new FileRecord
            {
                FileId                   = fileOldId,
                DirId                = subId,
                Name                 = "old.txt",
                Size                 = 1,
                Hash                 = hash,
                Modified             = DateTimeOffset.UtcNow,
                Created              = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status               = ScanEntryStatus.Enumerated,
                ErrorMessage         = null
            };

            // Initial delta: root, sub, old file (seen at sequence 1)
            repo.CommitDelta(new RepoDelta
            {
                ScanSequence = 1,
                Dirs         = [rootDir, subDir],
                Files        = [oldFile]
            });

            // Now simulate a later scan at sequence 2 that only sees a new file, not old one
            var newFile = new FileRecord
            {
                FileId                   = fileNewId,
                DirId                = subId,
                Name                 = "new.txt",
                Size                 = 2,
                Hash                 = hash,
                Modified             = DateTimeOffset.UtcNow,
                Created              = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 2,
                Status               = ScanEntryStatus.Enumerated,
                ErrorMessage         = null
            };

            repo.CommitDelta(new RepoDelta
            {
                ScanSequence = 2,
                Files        = [newFile]
            });

            // Complete scan: should tombstone the old file under the given root
            var rootPath = OperatingSystem.IsWindows() ? "root" : "/root";
            repo.CompleteScanForRoot(2, rootPath);

            // At this point, the snapshot should only have the new file
            var snapshot = repo.GetSnapshot();

            Assert.Single(snapshot.Files);
            Assert.True(snapshot.Files.ContainsKey(fileNewId));
            Assert.False(snapshot.Files.ContainsKey(fileOldId));
        }
        
        [Fact]
        public async Task SaveSnapshot_SetsLastSnapshottedLogSequenceToHighestLogId()
        {
            IRepo repo = await Repo.OpenAsync(_rootDir, TestContext.Current.CancellationToken);

            // 1. Ensure there is a ScanRoot with a bound DirId so SaveScanSnapshots
            //    actually has something to snapshot.
            var rootPath = OperatingSystem.IsWindows() ? "root" : "/root";
            _ = repo.BeginScan(rootPath); // creates a ScanRun + ScanRoot for this path

            var rootDirId = Guid.NewGuid();
            var seq = (repo as Repo)!.AllocateScanSequence();

            var rootDir = new DirRecord
            {
                DirId             = rootDirId,
                ParentId          = null,
                Name              = "root",
                LastSeenSequence  = seq,
                Status            = ScanEntryStatus.Enumerated,
                ErrorMessage      = null
            };

            await repo.CommitDeltaAsync(new RepoDelta
            {
                ScanSequence = seq,
                Dirs         = new List<DirRecord> { rootDir }
            }, TestContext.Current.CancellationToken);

            var scanRoot = Assert.Single(repo.ScanRootsView);
            (repo as Repo)!.BindScanRootDirId(scanRoot.Id, rootDirId);

            // 2. Force some log ids (these will be covered by the next snapshot save)
            await repo.CommitDeltaAsync(new RepoDelta { ScanSequence = seq }, TestContext.Current.CancellationToken);
            await repo.CommitDeltaAsync(new RepoDelta { ScanSequence = seq }, TestContext.Current.CancellationToken);

            // 3. Save snapshots and verify the invariant:
            //    LastSnapshottedLogSequence == NextLogSequence - 1
            repo.SaveScanSnapshots();

            var meta = ReadMeta();
            Assert.Equal(meta.NextLogSequence - 1, meta.LastSnapshottedLogSequence);
        }

        
        [Fact]
        public async Task HashIndex_ExposedAsReadOnlyDictionaryOfLists()
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "dff-repo-hashindex", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootDir);

            try
            {
                var repo = await Repo.OpenAsync(rootDir, TestContext.Current.CancellationToken);

                // Add a file with a hash so HashIndex is populated
                var dirId = Guid.NewGuid();
                var fileId = Guid.NewGuid();

                var dir = new DirRecord
                {
                    DirId = dirId,
                    ParentId = null,
                    Name = "root",
                    LastSeenSequence = 1,
                    Status = ScanEntryStatus.Enumerated,
                    ErrorMessage = null
                };

                var hashBytes = new byte[16];
                new Random(123).NextBytes(hashBytes);
                var hashKey = new HashKey(hashBytes);

                var file = new FileRecord
                {
                    FileId = fileId,
                    DirId = dirId,
                    Name = "file.txt",
                    Size = 123,
                    Hash = hashKey,
                    Modified = DateTimeOffset.UtcNow,
                    Created = DateTimeOffset.UtcNow,
                    LastSeenScanSequence = 1,
                    Status = ScanEntryStatus.Hashed,
                    ErrorMessage = null
                };

                await repo.CommitDeltaAsync(new RepoDelta
                {
                    ScanSequence = 1,
                    Dirs = new List<DirRecord> { dir },
                    Files = new List<FileRecord> { file }
                }, TestContext.Current.CancellationToken);

                var hashIndex = repo.GetSnapshot().HashIndex;

                Assert.True(hashIndex.ContainsKey(hashKey));
                var ids = hashIndex[hashKey];
                Assert.Contains(fileId, ids);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(rootDir))
                        Directory.Delete(rootDir, recursive: true);
                }
                catch
                {
                    // ignore cleanup errors
                }
            }
        }
    }
}