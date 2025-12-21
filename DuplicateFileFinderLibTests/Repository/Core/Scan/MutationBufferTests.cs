// DuplicateFileFinderLibTests/Repository/Core/Scan/MutationBufferTests.cs

using System.Linq;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLibTests.TestUtils.Fakes;
using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Core.Scan;

public sealed class MutationBufferTests
{
    [Fact]
    public void UpsertDir_WhenDirIdNotProvided_AllocatesId_AndSetsLastSeen()
    {
        var repo = new CapturingRepo { NextDirId = 1000, NextFileId = 2000, NextRunId = 3000 };
        var scanSeq = 77L;
        var buf = new MutationBuffer(repo, scanSeq);

        var input = new DirScanInput
        {
            DirId = -1,
            ParentDirId = 10,
            Name = "child",
            CreatedTicks = 1,
            ModifiedTicks = 2,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        };

        var dirId = buf.UpsertDir(input);

        Assert.Equal(1000L, dirId);
        Assert.Equal(1001L, repo.NextDirId);

        var snap = buf.BuildSnapshotV2(scanRootId: 1);
        Assert.Single(snap.Dirs);

        var d = snap.Dirs[0];
        Assert.Equal(1000L, d.DirId);
        Assert.Equal(10L, d.ParentDirId);
        Assert.Equal(scanSeq, d.LastSeenScanSequence);
        Assert.Equal(ScanEntryStatus.Enumerated, d.Status);
    }

    [Fact]
    public void UpsertDir_WhenDirIdAlreadyExists_UpdatesExistingRecord()
    {
        var repo = new CapturingRepo {NextDirId = 1000, NextFileId = 2000, NextRunId = 3000};
        var buf = new MutationBuffer(repo, scanSequence: 5);

        var id = buf.UpsertDir(new DirScanInput
        {
            DirId = 123,
            ParentDirId = 1,
            Name = "A",
            CreatedTicks = 10,
            ModifiedTicks = 11,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        });

        Assert.Equal(123, id);

        // Update same dirId with different values
        buf.UpsertDir(new DirScanInput
        {
            DirId = 123,
            ParentDirId = 2,
            Name = "A_renamed",
            CreatedTicks = 20,
            ModifiedTicks = 21,
            Status = ScanEntryStatus.Error,
            ErrorMessage = "boom"
        });

        var snap = buf.BuildSnapshotV2(scanRootId: 1);
        Assert.Single(snap.Dirs);

        var d = snap.Dirs[0];
        Assert.Equal(123, d.DirId);
        Assert.Equal(2, d.ParentDirId);
        Assert.Equal(ScanEntryStatus.Error, d.Status);
        Assert.NotEqual(-1, d.ErrorMessageStrIdx);
    }

    [Fact]
    public void UpsertFile_WhenFileIdNotProvided_AllocatesId_AndAddsKey()
    {
        var repo = new CapturingRepo {NextDirId = 1000, NextFileId = 2000, NextRunId = 3000};
        var scanSeq = 88L;
        var buf = new MutationBuffer(repo, scanSeq);

        buf.UpsertFile(new FileScanInput
        {
            FileId = -1,
            DirId = 10,
            Name = "x.bin",
            Size = 123,
            Hash = HashKey.NotComputed,
            CreatedTicks = 1,
            ModifiedTicks = 2,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        });

        Assert.Equal(2001L, repo.NextFileId);

        var snap = buf.BuildSnapshotV2(scanRootId: 1);
        Assert.Single(snap.Files);

        var f = snap.Files[0];
        Assert.Equal(2000, f.FileId);
        Assert.Equal(10, f.DirId);
        Assert.Equal(123, f.Size);
        Assert.Equal(HashKey.NotComputed, f.Hash);
        Assert.Equal(scanSeq, f.LastSeenScanSequence);
    }

    [Fact]
    public void UpsertFile_WhenFileIdAlreadyExists_UpdatesRecord_AndKeyMapIsUpdated()
    {
        var repo = new CapturingRepo{NextDirId = 1, NextFileId = 1, NextRunId = 1};
        var buf = new MutationBuffer(repo, scanSequence: 1);

        // Insert with known ID and name "a.txt"
        buf.UpsertFile(new FileScanInput
        {
            FileId = 500,
            DirId = 10,
            Name = "a.txt",
            Size = 1,
            Hash = HashKey.NotComputed,
            CreatedTicks = 0,
            ModifiedTicks = 0,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        });

        // Update same FileId but change name to "b.txt"
        buf.UpsertFile(new FileScanInput
        {
            FileId = 500,
            DirId = 10,
            Name = "b.txt",
            Size = 2,
            Hash = HashKey.NotComputed,
            CreatedTicks = 0,
            ModifiedTicks = 0,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        });

        // ApplyHash should hit the updated key (dirId=10, name="b.txt")
        var someHash = new HashKey([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        buf.ApplyFileHash(dirId: 10, name: "b.txt", hash: someHash);

        var snap = buf.BuildSnapshotV2(scanRootId: 1);
        Assert.Single(snap.Files);

        Assert.Equal(500, snap.Files[0].FileId);
        Assert.Equal(2, snap.Files[0].Size);
        Assert.Equal(someHash, snap.Files[0].Hash);
    }

    [Fact]
    public void ApplyFileHash_WhenFileExists_UpdatesHashOnly()
    {
        var repo = new CapturingRepo {NextDirId = 1, NextFileId = 1, NextRunId = 1};
        var buf = new MutationBuffer(repo, scanSequence: 9);

        buf.UpsertFile(new FileScanInput
        {
            FileId = 10,
            DirId = 42,
            Name = "f.dat",
            Size = 999,
            Hash = HashKey.NotComputed,
            CreatedTicks = 0,
            ModifiedTicks = 0,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        });

        var h = new HashKey(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        buf.ApplyFileHash(42, "f.dat", h);

        var snap = buf.BuildSnapshotV2(scanRootId: 1);
        var f = Assert.Single(snap.Files);
        Assert.Equal(h, f.Hash);
        Assert.Equal(999, f.Size); // unchanged
        Assert.Equal(ScanEntryStatus.Enumerated, f.Status); // unchanged
    }

    [Fact]
    public void ApplyFileHash_WhenFileMissing_CreatesErrorEntry_WithGivenHash()
    {
        var repo = new CapturingRepo{NextDirId= 1, NextFileId= 100, NextRunId= 1};
        var buf = new MutationBuffer(repo, scanSequence: 1);

        var h = new HashKey(Enumerable.Repeat((byte)7, 16).ToArray());
        buf.ApplyFileHash(dirId: 9, name: "missing.bin", hash: h);

        var snap = buf.BuildSnapshotV2(scanRootId: 1);
        var f = Assert.Single(snap.Files);

        Assert.Equal(100, f.FileId);                 // allocated
        Assert.Equal(9, f.DirId);
        Assert.Equal(h, f.Hash);
        Assert.Equal(ScanEntryStatus.Error, f.Status);
        Assert.NotEqual(-1, f.ErrorMessageStrIdx);
    }

    [Fact]
    public void ApplyFileError_WhenFileExists_SetsStatusError_AndErrorMessage()
    {
        var repo = new CapturingRepo{NextDirId = 1, NextFileId = 1, NextRunId = 1};
        var buf = new MutationBuffer(repo, scanSequence: 1);

        buf.UpsertFile(new FileScanInput
        {
            FileId = 10,
            DirId = 42,
            Name = "f.dat",
            Size = 1,
            Hash = HashKey.NotComputed,
            CreatedTicks = 0,
            ModifiedTicks = 0,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        });

        buf.ApplyFileError(42, "f.dat", "read failed");

        var snap = buf.BuildSnapshotV2(scanRootId: 1);
        var f = Assert.Single(snap.Files);

        Assert.Equal(ScanEntryStatus.Error, f.Status);
        Assert.NotEqual(-1, f.ErrorMessageStrIdx);
    }

    [Fact]
    public void ApplyFileError_WhenFileMissing_CreatesErrorEntry_WithNotComputedHash()
    {
        var repo = new CapturingRepo{NextDirId = 1, NextFileId = 500, NextRunId = 1};
        var buf = new MutationBuffer(repo, scanSequence: 1);

        buf.ApplyFileError(dirId: 9, name: "missing.bin", errorMessage: "no access");

        var snap = buf.BuildSnapshotV2(scanRootId: 1);
        var f = Assert.Single(snap.Files);

        Assert.Equal(500, f.FileId);
        Assert.Equal(9, f.DirId);
        Assert.Equal(HashKey.NotComputed, f.Hash);
        Assert.Equal(ScanEntryStatus.Error, f.Status);
        Assert.NotEqual(-1, f.ErrorMessageStrIdx);
    }

    [Fact]
    public void BuildSnapshotV2_FiltersOutStatusNone_ForDirsAndFiles()
    {
        var repo = new CapturingRepo{NextDirId = 1, NextFileId = 1, NextRunId = 1};
        var buf = new MutationBuffer(repo, scanSequence: 1);

        buf.UpsertDir(new DirScanInput
        {
            DirId = 1,
            ParentDirId = 0,
            Name = "keep-dir",
            CreatedTicks = 0,
            ModifiedTicks = 0,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        });

        buf.UpsertDir(new DirScanInput
        {
            DirId = 2,
            ParentDirId = 0,
            Name = "drop-dir",
            CreatedTicks = 0,
            ModifiedTicks = 0,
            Status = ScanEntryStatus.None,
            ErrorMessage = null
        });

        buf.UpsertFile(new FileScanInput
        {
            FileId = 1,
            DirId = 1,
            Name = "keep-file",
            Size = 0,
            Hash = HashKey.NotComputed,
            CreatedTicks = 0,
            ModifiedTicks = 0,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        });

        buf.UpsertFile(new FileScanInput
        {
            FileId = 2,
            DirId = 1,
            Name = "drop-file",
            Size = 0,
            Hash = HashKey.NotComputed,
            CreatedTicks = 0,
            ModifiedTicks = 0,
            Status = ScanEntryStatus.None,
            ErrorMessage = null
        });

        var snap = buf.BuildSnapshotV2(scanRootId: 1);

        Assert.Single(snap.Dirs);
        Assert.Single(snap.Files);

        Assert.Equal(1, snap.Dirs[0].DirId);
        Assert.Equal(1, snap.Files[0].FileId);
    }

    [Fact]
    public void FileKeyComparer_UsesPathComparer_ForNameEquality()
    {
        var repo = new CapturingRepo{NextDirId = 1, NextFileId = 1, NextRunId = 1};
        var buf = new MutationBuffer(repo, scanSequence: 1);

        buf.UpsertFile(new FileScanInput
        {
            FileId = 10,
            DirId = 5,
            Name = "Case.TXT",
            Size = 1,
            Hash = HashKey.NotComputed,
            CreatedTicks = 0,
            ModifiedTicks = 0,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        });

        // Different FileId, same dir, name differs only by case.
        buf.UpsertFile(new FileScanInput
        {
            FileId = 11,
            DirId = 5,
            Name = "case.txt",
            Size = 2,
            Hash = HashKey.NotComputed,
            CreatedTicks = 0,
            ModifiedTicks = 0,
            Status = ScanEntryStatus.Enumerated,
            ErrorMessage = null
        });

        // If they compare equal, ApplyFileHash should update whichever one the key maps to (latest insertion overwrites key).
        var h = new HashKey(Enumerable.Repeat((byte)9, 16).ToArray());
        buf.ApplyFileHash(5, "CASE.txt", h);

        var snap = buf.BuildSnapshotV2(scanRootId: 1);

        // At least one file should have the updated hash
        Assert.Contains(snap.Files, f => f.Hash.Equals(h));
    }
}
