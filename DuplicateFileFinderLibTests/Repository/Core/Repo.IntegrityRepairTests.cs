using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

using DuplicateFileFinderLibTests.TestUtils;

using MemoryPack;

using Xunit;

namespace DuplicateFileFinderLibTests.Repository.Core;

public sealed class RepoIntegrityRepairTests
{
    private readonly TempFsFixture _temp = new("DFF_RepoIntegrityRepair");

    [Fact]
    public async Task RepairMigratedRepoAsync_PromotesDirsWithMissingParent_ToRoot()
    {
        var repoDir = _temp.Dir("repo");

        // ------------------------------
        // Arrange meta
        // ------------------------------
        var scanRootId = 1L;

        var meta = NewMeta(repoDir);

        var root = new ScanRoot
        {
            RootId = scanRootId,
            RootPath = "data",
            DirId = 100,
            CreatedAt = DateTimeOffset.UtcNow,
            LastScannedAt = DateTimeOffset.UtcNow,
            VolumePath = "/mnt",
            IsDeleted = false
        };

        var run = new ScanRun
        {
            ScanRootId = scanRootId,
            ScanSequence = 10,
            RootPath = "/mnt/data",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            FinishedAt = DateTimeOffset.UtcNow,
            Status = ScanRunStatus.Completed,
            HashPolicy = HashPolicyMode.Default
        };

        var metaFile = new RepoMetaFile
        {
            Meta = meta,
            ScanRoots = [root],
            ScanRuns = [run]
        };

        await WriteMpAsync(Path.Combine(repoDir, "repo.mp"), metaFile);

        // ------------------------------
        // Arrange snapshot with broken parent
        // ------------------------------
        var rootsDir = Path.Combine(repoDir, "roots");
        Directory.CreateDirectory(rootsDir);

        // pool indices: 0="root", 1="child"
        var pool = CreateStringPool("root", "child");

        var snap = new ScanRootSnapshotV2
        {
            ScanRootId = scanRootId,
            StringPool = pool,
            Dirs =
            [
                new DirRecordV2
        {
            DirId = 100,
            ParentDirId = -1,
            NameStrIdx = 0,
            LastSeenScanSequence = 10,
            Status = ScanEntryStatus.Enumerated
                },
                new DirRecordV2
        {
            DirId = 200,
            ParentDirId = 999, // missing
            NameStrIdx = 1,
            LastSeenScanSequence = 10,
            Status = ScanEntryStatus.Enumerated
                }
            ],
            Files = []
        };

        await WriteMpAsync(Path.Combine(rootsDir, "1.mp"), snap);

        // ------------------------------
        // Act
        // ------------------------------
        var repo = await Repo.OpenAsync(repoDir, TestContext.Current.CancellationToken);
        try
        {
            await repo.RepairRepoAsync(TestContext.Current.CancellationToken);

            // Assert: snapshot updated in-memory
            var view = repo.TryGetScanRootView(scanRootId);
            Assert.NotNull(view);

            var repaired = view.Dirs.Single(d => d.DirId == 200);
            Assert.Equal(-1, repaired.ParentDirId);
        }
        finally
        {
            await repo.DisposeAsync();
        }
    }

    [Fact]
    public async Task RepairMigratedRepoAsync_DedupesRoots_DeletesOrphanSnapshots()
    {
        var repoDir = _temp.Dir("repo");

        var meta = NewMeta(repoDir);

        var r1 = new ScanRoot
        {
            RootId = 1,
            RootPath = "data",
            DirId = 0,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-5),
            LastScannedAt = DateTimeOffset.UtcNow.AddDays(-4),
            VolumePath = "/mnt",
            IsDeleted = false
        };

        var r2 = new ScanRoot
        {
            RootId = 2,
            RootPath = "data",
            DirId = 500,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
            LastScannedAt = DateTimeOffset.UtcNow.AddDays(-1),
            VolumePath = "/mnt",
            IsDeleted = false
        };

        var run1 = new ScanRun
        {
            ScanRootId = 1,
            ScanSequence = 101,
            RootPath = "/mnt/data",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-5),
            FinishedAt = DateTimeOffset.UtcNow.AddHours(-4),
            Status = ScanRunStatus.Completed,
            HashPolicy = HashPolicyMode.Default
        };

        var run2 = run1 with { ScanRootId = 2, ScanSequence = 102 };

        var metaFile = new RepoMetaFile
        {
            Meta = meta,
            ScanRoots = [r1, r2],
            ScanRuns = [run1, run2]
        };

        await WriteMpAsync(Path.Combine(repoDir, "repo.mp"), metaFile);

        // Arrange: roots snapshots for both ids exist on disk
        var rootsDir = Path.Combine(repoDir, "roots");
        Directory.CreateDirectory(rootsDir);

        var pool = CreateStringPool("data");

        await WriteMpAsync(Path.Combine(rootsDir, "1.mp"),
            new ScanRootSnapshotV2
            {
                ScanRootId = 1,
                StringPool = pool,
                Dirs =
                [
                    new DirRecordV2 { DirId = 111, ParentDirId = -1, NameStrIdx = 0, LastSeenScanSequence = 101 }
                ],
                Files = []
            });

        await WriteMpAsync(Path.Combine(rootsDir, "2.mp"),
            new ScanRootSnapshotV2
            {
                ScanRootId = 2,
                StringPool = pool,
                Dirs =
                [
                    new DirRecordV2 { DirId = 222, ParentDirId = -1, NameStrIdx = 0, LastSeenScanSequence = 102 }
                ],
                Files = []
            });

        var repo = await Repo.OpenAsync(repoDir, TestContext.Current.CancellationToken);
        try
        {
            await repo.RepairRepoAsync(TestContext.Current.CancellationToken);

            var roots = repo.ScanRootsView.Where(r => !r.IsDeleted).ToList();
            Assert.Single(roots);
            Assert.Equal(2, roots[0].RootId);

            Assert.All(repo.ScanRunsView, r => Assert.Equal(2, r.ScanRootId));

            Assert.False(File.Exists(Path.Combine(rootsDir, "1.mp")));
            Assert.True(File.Exists(Path.Combine(rootsDir, "2.mp")));
        }
        finally
        {
            await repo.DisposeAsync();
        }
    }

    // --------------------------------------------------------------------
    // Orphan ScanRuns removal
    // --------------------------------------------------------------------

    [Fact]
    public async Task RepairMigratedRepoAsync_RemovesOrphanScanRuns_NotReferencedByAnySnapshotSequence()
    {
        var repoDir = _temp.Dir("repo");

        var meta = NewMeta(repoDir);

        var root = new ScanRoot
        {
            RootId = 1,
            RootPath = "data",
            DirId = 100,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            LastScannedAt = DateTimeOffset.UtcNow.AddDays(-1),
            VolumePath = "/mnt",
            IsDeleted = false
        };

        // Two runs, but only seq=101 will appear in snapshots.
        var runUsed = NewRun(1, 101, "/mnt/data", ScanRunStatus.Completed);
        var runOrphan = NewRun(1, 102, "/mnt/data", ScanRunStatus.Completed);

        var metaFile = new RepoMetaFile
        {
            Meta = meta,
            ScanRoots = [root],
            ScanRuns = [runUsed, runOrphan]
        };

        await WriteMpAsync(Path.Combine(repoDir, "repo.mp"), metaFile);

        var rootsDir = Path.Combine(repoDir, "roots");
        Directory.CreateDirectory(rootsDir);

        var pool = CreateStringPool("data");

        // Snapshot references only seq=101.
        await WriteMpAsync(Path.Combine(rootsDir, "1.mp"),
            new ScanRootSnapshotV2
            {
                ScanRootId = 1,
                StringPool = pool,
                Dirs =
                [
                    new DirRecordV2 { DirId = 100, ParentDirId = -1, NameStrIdx = 0, LastSeenScanSequence = 101 }
                ],
                Files = []
            });

        var repo = await Repo.OpenAsync(repoDir, TestContext.Current.CancellationToken);
        try
        {
            await repo.RepairRepoAsync(TestContext.Current.CancellationToken);

            var seqs = repo.ScanRunsView.Select(r => r.ScanSequence).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 101L }, seqs);
        }
        finally
        {
            await repo.DisposeAsync();
        }
    }

    [Fact]
    public async Task RepairMigratedRepoAsync_FillsMissingScanRootDirId_FromSnapshotRootDir()
    {
        var repoDir = _temp.Dir("repo");

        var meta = NewMeta(repoDir);

        var root = new ScanRoot
        {
            RootId = 1,
            RootPath = "data",
            DirId = 0, // missing, should be repaired
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            LastScannedAt = DateTimeOffset.UtcNow.AddDays(-1),
            VolumePath = "/mnt",
            IsDeleted = false
        };

        var run = NewRun(1, 101, "/mnt/data", ScanRunStatus.Completed);

        var metaFile = new RepoMetaFile
        {
            Meta = meta,
            ScanRoots = [root],
            ScanRuns = [run]
        };

        await WriteMpAsync(Path.Combine(repoDir, "repo.mp"), metaFile);

        var rootsDir = Path.Combine(repoDir, "roots");
        Directory.CreateDirectory(rootsDir);

        var pool = CreateStringPool("data");

        const long snapshotRootDirId = 777;

        await WriteMpAsync(Path.Combine(rootsDir, "1.mp"),
            new ScanRootSnapshotV2
            {
                ScanRootId = 1,
                StringPool = pool,
                Dirs =
                [
                    new DirRecordV2
                    {
                        DirId = snapshotRootDirId,
                        ParentDirId = -1,
                        NameStrIdx = 0,
                        LastSeenScanSequence = 101,
                        Status = ScanEntryStatus.Enumerated
                    }
                ],
                Files = Array.Empty<FileRecordV2>()
            });

        var repo = await Repo.OpenAsync(repoDir, TestContext.Current.CancellationToken);
        try
        {
            await repo.RepairRepoAsync(TestContext.Current.CancellationToken);

            var repairedRoot = Assert.Single(repo.ScanRootsView, r => r.RootId == 1);
            Assert.True(repairedRoot.DirId > 0);
            Assert.Equal(snapshotRootDirId, repairedRoot.DirId);
        }
        finally
        {
            await repo.DisposeAsync();
        }
    }

    // ---------------- helpers ----------------

    private static RepoMeta NewMeta(string repoDir) => new RepoMeta
    {
        SchemaVersion = 6,
        Generation = 1,
        RepoId = Guid.NewGuid(),
        RepoPath = repoDir,
        RepoHostName = Environment.MachineName,
        NextScanSequence = 1,
        NextScanRootId = 10,
        NextDirId = 1000,
        NextFileId = 2000
    };

    private static async Task WriteMpAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await MemoryPackSerializer.SerializeAsync(fs, value);
        await fs.FlushAsync();
    }

    private static PackedStringPool CreateStringPool(params string[] strings)
    {
        var offsets = new int[strings.Length + 1];
        var total = strings.Sum(s => Encoding.UTF8.GetByteCount(s));
        var data = new byte[total];

        int cursor = 0;
        for (int i = 0; i < strings.Length; i++)
        {
            offsets[i] = cursor;
            cursor += Encoding.UTF8.GetBytes(strings[i], 0, strings[i].Length, data, cursor);
        }
        offsets[strings.Length] = cursor;

        return new PackedStringPool(data, offsets);
    }

    private static ScanRun NewRun(long scanRootId, long seq, string rootPath, ScanRunStatus status)
        => new ScanRun
        {
            ScanRootId = scanRootId,
            ScanSequence = seq,
            RootPath = rootPath,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            Status = status,
            ErrorMessage = null,
            HashPolicy = HashPolicyMode.Default
        };
}
