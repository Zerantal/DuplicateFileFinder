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
            var id = Guid.NewGuid();
            var dirId = Guid.NewGuid();

            var hashBytes = new byte[16];
            new Random(42).NextBytes(hashBytes);
            var hashKey = new HashKey(hashBytes);

            var original = new FileRecord
            {
                Id = id,
                DirId = dirId,
                Name = "foo.txt",
                Size = 1234,
                Hash = hashKey,
                Modified = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
                Created = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                LastSeenScanSequence = 99,
                Status = ScanEntryStatus.Enumerated | ScanEntryStatus.Hashed,
                ErrorMessage = "some error"
            };

            var bytes = MemoryPackSerializer.Serialize(original);
            var roundTripped = MemoryPackSerializer.Deserialize<FileRecord>(bytes)!;

            Assert.Equal(original.Id, roundTripped.Id);
            Assert.Equal(original.DirId, roundTripped.DirId);
            Assert.Equal(original.Name, roundTripped.Name);
            Assert.Equal(original.Size, roundTripped.Size);
            Assert.Equal(original.Hash, roundTripped.Hash);
            Assert.Equal(original.Modified, roundTripped.Modified);
            Assert.Equal(original.Created, roundTripped.Created);
            Assert.Equal(original.LastSeenScanSequence, roundTripped.LastSeenScanSequence);
            Assert.Equal(original.Status, roundTripped.Status);
            Assert.Equal(original.ErrorMessage, roundTripped.ErrorMessage);
        }

        [Fact]
        public void FileRecord_StatusFlags_ComposeAsExpected()
        {
            var status = ScanEntryStatus.Enumerated | ScanEntryStatus.Hashed | ScanEntryStatus.SkippedByFilter;

            Assert.True(status.HasFlag(ScanEntryStatus.Enumerated));
            Assert.True(status.HasFlag(ScanEntryStatus.Hashed));
            Assert.True(status.HasFlag(ScanEntryStatus.SkippedByFilter));
            Assert.False(status.HasFlag(ScanEntryStatus.Error));
            Assert.False(status.HasFlag(ScanEntryStatus.Deleted));
        }
    }