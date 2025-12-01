using System;
using System.Collections.Generic;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class RepoDeltaTests
    {
        [Fact]
        public void RepoDelta_DefaultLists_AreNonNullAndEmpty()
        {
            var delta = new RepoDelta {RunId = 99};

            Assert.NotNull(delta.Files);
            Assert.NotNull(delta.Dirs);
            Assert.NotNull(delta.DeletedFiles);
            Assert.NotNull(delta.DeletedDirs);

            Assert.Empty(delta.Files);
            Assert.Empty(delta.Dirs);
            Assert.Empty(delta.DeletedFiles);
            Assert.Empty(delta.DeletedDirs);
        }

        [Fact]
        public void RepoDelta_MemoryPackRoundTrip_PreservesCollections()
        {
            var dirId = 11;
            var fileId1 = 22;
            var fileId2 = 33;
            var scanSequence = 3;

            var hashBytes = new byte[16];
            new Random(999).NextBytes(hashBytes);
            var hashKey = new HashKey(hashBytes);

            var dir = new DirRecord
            {
                DirId = dirId,
                ParentId = null,
                Name = "root",
                SeenDuringScanRunId = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            var file1 = new FileRecord
            {
                FileId = fileId1,
                DirId = dirId,
                Name = "f1",
                Size = 10,
                Hash = hashKey,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                SeenDuringSeenScanRunId = 1,
                Status = ScanEntryStatus.Enumerated,
                ErrorMessage = null
            };

            var file2 = new FileRecord
            {
                FileId = fileId2,
                DirId = dirId,
                Name = "f2",
                Size = 20,
                Hash = hashKey,
                Modified = DateTimeOffset.UtcNow,
                Created = DateTimeOffset.UtcNow,
                SeenDuringSeenScanRunId = 1,
                Status = ScanEntryStatus.Hashed,
                ErrorMessage = "err"
            };

            var tombFile = new FileTombstone(fileId1, 5);
            var tombDir = new DirTombstone(dirId, 5);

            var original = new RepoDelta
            {
                Files = new List<FileRecord> { file1, file2 },
                Dirs = new List<DirRecord> { dir },
                DeletedFiles = new List<FileTombstone> { tombFile },
                DeletedDirs = new List<DirTombstone> { tombDir },
                RunId = scanSequence
            };

            var bytes = MemoryPackSerializer.Serialize(original);
            var roundTripped = MemoryPackSerializer.Deserialize<RepoDelta>(bytes)!;

            Assert.Equal(original.Files.Count, roundTripped.Files.Count);
            Assert.Equal(original.Dirs.Count, roundTripped.Dirs.Count);
            Assert.Equal(original.DeletedFiles.Count, roundTripped.DeletedFiles.Count);
            Assert.Equal(original.DeletedDirs.Count, roundTripped.DeletedDirs.Count);
            Assert.Equal(original.RunId, roundTripped.RunId);

            Assert.Contains(roundTripped.Files, f => f.FileId == fileId1);
            Assert.Contains(roundTripped.Files, f => f.FileId == fileId2);
            Assert.Contains(roundTripped.Dirs, d => d.DirId == dirId);
            Assert.Contains(roundTripped.DeletedFiles, t => t.FileId == fileId1);
            Assert.Contains(roundTripped.DeletedDirs, t => t.DirId == dirId);
        }
    }