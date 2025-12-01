using System;
using DuplicateFileFinderLib.Repository.Models;
using MemoryPack;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Models;

public sealed class FileRecordTests
    {
        [Fact]
        public void FileRecord_MemoryPackRoundTrip_PreservesAllFields()
        {
            var id = 44;
            var dirId = 55;

            var hashBytes = new byte[16];
            new Random(42).NextBytes(hashBytes);
            var hashKey = new HashKey(hashBytes);

            var original = new FileRecord
            {
                FileId = id,
                DirId = dirId,
                Name = "foo.txt",
                Size = 1234,
                Hash = hashKey,
                Modified = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
                Created = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                SeenDuringSeenScanRunId = 99,
                Status = ScanEntryStatus.Hashed,
                ErrorMessage = "some error"
            };

            var bytes = MemoryPackSerializer.Serialize(original);
            var roundTripped = MemoryPackSerializer.Deserialize<FileRecord>(bytes)!;

            Assert.Equal(original.FileId, roundTripped.FileId);
            Assert.Equal(original.DirId, roundTripped.DirId);
            Assert.Equal(original.Name, roundTripped.Name);
            Assert.Equal(original.Size, roundTripped.Size);
            Assert.Equal(original.Hash, roundTripped.Hash);
            Assert.Equal(original.Modified, roundTripped.Modified);
            Assert.Equal(original.Created, roundTripped.Created);
            Assert.Equal(original.SeenDuringSeenScanRunId, roundTripped.SeenDuringSeenScanRunId);
            Assert.Equal(original.Status, roundTripped.Status);
            Assert.Equal(original.ErrorMessage, roundTripped.ErrorMessage);
        }
    }