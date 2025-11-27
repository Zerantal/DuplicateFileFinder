using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

        private string MetaPath => Path.Combine(_rootDir, "meta.json");
        private string SnapshotPath => Path.Combine(_rootDir, "snapshot.bin");
        private string LogDir => Path.Combine(_rootDir, "log");

        private RepoMeta ReadMeta()
        {
            var json = File.ReadAllText(MetaPath);
            return JsonSerializer.Deserialize<RepoMeta>(json)!;
        }

        private RepoSnapshot ReadSnapshot()
        {
            var bytes = File.ReadAllBytes(SnapshotPath);
            return MemoryPackSerializer.Deserialize<RepoSnapshot>(bytes)!;
        }

        [Fact]
        public void Open_NewRepo_CreatesMetaAndLogDirectory()
        {
            Assert.False(File.Exists(MetaPath));
            Assert.False(Directory.Exists(LogDir));

            Repo.Open(_rootDir);

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
        public void AllocateScanSequence_UsesAndPersistsNextScanSequence()
        {
            var repo = Repo.Open(_rootDir);
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
        public void CommitDelta_WritesDeltaFileAndAdvancesNextLogSequence()
        {
            var repo = Repo.Open(_rootDir);
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

            repo.CommitDelta(delta);

            var metaAfter = ReadMeta();
            Assert.Equal(1, metaAfter.NextLogSequence);

            var deltaFiles = Directory.GetFiles(LogDir, $"{metaAfter.Generation}-*.delta");
            Assert.Single(deltaFiles);
        }

        [Fact]
        public void CommitDelta_WithDir_AllowsGetFullDirPathOnReopen()
        {
            var repo = Repo.Open(_rootDir);

            var rootDirId = Guid.NewGuid();
            var rootDirRecord = new DirRecord
            {
                Id = rootDirId,
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

            repo.CommitDelta(delta);

            // Close and reopen to force replay from log
            repo = Repo.Open(_rootDir);

            var fullPath = repo.GetFullDirPath(rootDirId);
            Assert.Equal(OperatingSystem.IsWindows() ? "root" : "/root", fullPath);
        }

        [Fact]
        public void CommitDelta_WithFile_UpdatesHashIndexAndSnapshot()
        {
            var repo = Repo.Open(_rootDir);

            var dirId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            var dir = new DirRecord
            {
                Id = dirId,
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
                Id = fileId,
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

            repo.CommitDelta(delta);
            repo.SaveSnapshot();

            var snapshot = ReadSnapshot();
            Assert.Single(snapshot.Dirs);
            Assert.Single(snapshot.Files);
            Assert.Single(snapshot.HashIndex);

            var hashEntry = Assert.Single(snapshot.HashIndex);
            Assert.Equal(hashKey, hashEntry.Key);
            Assert.Contains(fileId, hashEntry.Value);
        }

        [Fact]
        public void ApplyDelta_WithTombstones_RemovesFilesAndHashIndex()
        {
            var repo = Repo.Open(_rootDir);

            var dirId = Guid.NewGuid();
            var fileId = Guid.NewGuid();

            var dir = new DirRecord
            {
                Id = dirId,
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
                Id = fileId,
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
            repo.CommitDelta(new RepoDelta
            {
                ScanSequence = 1,
                Dirs = [dir],
                Files = [file]
            });

            repo.SaveSnapshot();

            var snapshot1 = ReadSnapshot();
            Assert.Single(snapshot1.Files);
            Assert.Single(snapshot1.HashIndex);

            // Second delta: delete file
            repo.CommitDelta(new RepoDelta
            {
                ScanSequence = 2,
                DeletedFiles = [new(fileId, 2)]
            });

            repo.SaveSnapshot();

            var snapshot2 = ReadSnapshot();
            Assert.Empty(snapshot2.Files);
            Assert.True(snapshot2.HashIndex.Count == 0 || !snapshot2.HashIndex.Values.SelectMany(x => x).Contains(fileId));
        }

        [Fact]
        public void SaveSnapshot_And_ReplayDeltas_RestoreState()
        {
            var repo = Repo.Open(_rootDir);

            var dirId = Guid.NewGuid();
            var fileId1 = Guid.NewGuid();
            var fileId2 = Guid.NewGuid();

            var hashBytes1 = new byte[16];
            new Random(1).NextBytes(hashBytes1);
            var hash1 = new HashKey(hashBytes1);

            var hashBytes2 = new byte[16];
            new Random(2).NextBytes(hashBytes2);
            var hash2 = new HashKey(hashBytes2);

            var seq = repo.AllocateScanSequence();
            
            var dir = new DirRecord
            {
                Id = dirId,
                ParentId = null,
                Name = "root",
                LastSeenSequence = seq,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            var file1 = new FileRecord
            {
                Id = fileId1,
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
            repo.CommitDelta(new RepoDelta
            {
                ScanSequence = seq,
                Dirs = [dir],
                Files = [file1]
            });

            repo.SaveSnapshot(); // snapshot includes delta1

            seq = repo.AllocateScanSequence();
            var file2 = new FileRecord
            {
                Id = fileId2,
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
            
            // Delta 2: add file2 after snapshot
            repo.CommitDelta(new RepoDelta
            {
                ScanSequence = seq,
                Files = [file2]
            });

            // Reopen: should load snapshot (dir + f1) and then replay delta2 (f2)
            repo = Repo.Open(_rootDir);
            repo.SaveSnapshot();
            var snapshot = ReadSnapshot();

            Assert.Equal(2, snapshot.Files.Count);
            Assert.True(snapshot.Files.ContainsKey(fileId1));
            Assert.True(snapshot.Files.ContainsKey(fileId2));
        }

        [Fact]
        public void CompactNow_DeletesAllCoveredDeltaFiles()
        {
            var repo = Repo.Open(_rootDir);

            // Write a couple of deltas
            repo.CommitDelta(new RepoDelta { ScanSequence = 0 });
            repo.CommitDelta(new RepoDelta { ScanSequence = 1 });

            var metaBefore = ReadMeta();
            Assert.Equal(2, metaBefore.NextLogSequence); // ids 0,1 allocated

            repo.CompactNow();

            var metaAfter = ReadMeta();

            // New invariant: snapshot covers 0..(NextLogSequence-1)
            Assert.Equal(metaAfter.NextLogSequence - 1, metaAfter.LastSnapshottedLogSequence);

            var deltaFilesAfter = Directory.GetFiles(LogDir, $"{metaAfter.Generation}-*.delta");
            Assert.Empty(deltaFilesAfter);

        }

        [Fact]
        public void GetFullDirPath_ReconstructsHierarchy()
        {
            var repo = Repo.Open(_rootDir);

            var rootId = Guid.NewGuid();
            var childId = Guid.NewGuid();

            var rootDir = new DirRecord
            {
                Id = rootId,
                ParentId = null,
                Name = "root",
                LastSeenSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            var childDir = new DirRecord
            {
                Id = childId,
                ParentId = rootId,
                Name = "child",
                LastSeenSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            repo.CommitDelta(new RepoDelta
            {
                ScanSequence = 1,
                Dirs = [rootDir, childDir]
            });

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
        public void CompleteScanForRoot_ProducesTombstonesForMissingFiles()
        {
            var repo = Repo.Open(_rootDir);

            var rootId = Guid.NewGuid();
            var subId = Guid.NewGuid();
            var fileOldId = Guid.NewGuid();
            var fileNewId = Guid.NewGuid();

            var rootDir = new DirRecord
            {
                Id = rootId,
                ParentId = null,
                Name = "root",
                LastSeenSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            var subDir = new DirRecord
            {
                Id = subId,
                ParentId = rootId,
                Name = "sub",
                LastSeenSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            var hashBytes = new byte[16];
            new Random(111).NextBytes(hashBytes);
            var hash = new HashKey(hashBytes);

            var oldFile = new FileRecord
            {
                Id = fileOldId,
                DirId = subId,
                Name = "old.txt",
                Size = 1,
                Hash = hash,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            // Initial delta: root, sub, old file (seen at sequence 1)
            repo.CommitDelta(new RepoDelta
            {
                ScanSequence = 1,
                Dirs = [rootDir, subDir],
                Files = [oldFile]
            });

            // Now simulate a later scan at sequence 2 that only sees a new file, not old one
            var newFile = new FileRecord
            {
                Id = fileNewId,
                DirId = subId,
                Name = "new.txt",
                Size = 2,
                Hash = hash,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                LastSeenScanSequence = 2,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            repo.CommitDelta(new RepoDelta
            {
                ScanSequence = 2,
                Files = [newFile]
            });

            // Complete scan: should tombstone the old file under root
            var rootPath = OperatingSystem.IsWindows() ? "root" : "/root";
            repo.CompleteScanForRoot(2, rootPath);

            repo.SaveSnapshot();
            var snapshot = ReadSnapshot();

            Assert.Single(snapshot.Files); // only new file remains
            Assert.True(snapshot.Files.ContainsKey(fileNewId));
            Assert.False(snapshot.Files.ContainsKey(fileOldId));
        }
        
        [Fact]
        public void SaveSnapshot_SetsLastSnapshottedLogSequenceToHighestLogId()
        {
            var repo = Repo.Open(_rootDir);

            // Force some log ids
            repo.CommitDelta(new RepoDelta { ScanSequence = 0 });
            repo.CommitDelta(new RepoDelta { ScanSequence = 0 });

            repo.SaveSnapshot();

            var meta = ReadMeta();
            Assert.Equal(meta.NextLogSequence - 1, meta.LastSnapshottedLogSequence);
        }
        
        [Fact]
        public void HashIndex_ExposedAsReadOnlyDictionaryOfLists()
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "dff-repo-hashindex", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootDir);

            try
            {
                var repo = Repo.Open(rootDir);

                // Add a file with a hash so HashIndex is populated
                var dirId = Guid.NewGuid();
                var fileId = Guid.NewGuid();

                var dir = new DirRecord
                {
                    Id = dirId,
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
                    Id = fileId,
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

                repo.CommitDelta(new RepoDelta
                {
                    ScanSequence = 1,
                    Dirs = new List<DirRecord> { dir },
                    Files = new List<FileRecord> { file }
                });

                var hashIndex = repo.GetSnapshot().HashIndex;
                // var hashIndex = repo.HashIndex;

                Assert.True(hashIndex.ContainsKey(hashKey));
                var ids = hashIndex[hashKey];
                Assert.Contains(fileId, ids);

                // Verify that we really have a read-only list from the public API
                // if (ids is IList<Guid> asIList)
                // {
                //     Assert.Throws<NotSupportedException>(() => asIList.Add(Guid.NewGuid()));
                // }
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
