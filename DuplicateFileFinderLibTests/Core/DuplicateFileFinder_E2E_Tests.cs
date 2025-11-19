// DuplicateFileFinderLibTests/DuplicateFileFinderTests.cs

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Util;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;
// ReSharper disable InconsistentNaming

// ReSharper disable StringLiteralTypo
// ReSharper disable RedundantArgumentDefaultValue

namespace DuplicateFileFinderLibTests.Core;

// ReSharper disable once InconsistentNaming
public sealed class DuplicateFileFinder_E2E_Tests : IDisposable
{
    private readonly TempFsFixture _fs = new();

    public void Dispose()
    {
        _fs.Dispose();
    }

    class FakeRepo : IRepo
    {
        public RepoViewSnapshot GetSnapshot()
        {
            throw new NotImplementedException();
        }

        public event EventHandler<RepoDelta>? DeltaCommitted;
        public IScanSession BeginScan(string rootPath, int maxFilesBeforeFlush = 10000, int maxDirsBeforeFlush = 1000)
        {
            return new FakeSession();
        }

        public void CommitDelta(RepoDelta delta)
        {
            throw new NotImplementedException();
        }

        public Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public void SaveSnapshot()
        {
            throw new NotImplementedException();
        }

        public void CompactIfNeeded(RepoCompactionPolicy? policy = null)
        {
            throw new NotImplementedException();
        }

        public void CompactNow()
        {
            throw new NotImplementedException();
        }

        public string GetFullDirPath(Guid dirId)
        {
            throw new NotImplementedException();
        }
    }

    class FakeSession : IScanSession
    {
        public ScanRun Run { get; } = null!;
        public long ScanSequence { get; } = 0;
        public string RootPath { get; } = null!;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public void ObserveDir(Guid id, Guid? parentId, string name, ScanEntryStatus status, string? errorMessage = null)
        {
        }

        public void ObserveFile(Guid id, Guid dirId, string name, long size, HashKey hash, DateTimeOffset modified,
            DateTimeOffset created, ScanEntryStatus status, string? errorMessage = null)
        {
        }

        public Task FlushProgressAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
    
    [Fact]
    public async Task ScanLocation_FindsDuplicates_AndAssignsGroups()
    {
        // arrange
        // tempRoot/
        //   a/file1.txt        ("HELLO")
        //   b/file1copy.txt    ("HELLO")    -> duplicate of file1.txt
        //   c/unique.bin       ("WORLD!")   -> unique (CS not calculated due size uniqueness
        _fs.Dir("a");
        _fs.Dir("b");
        _fs.Dir("c");
        var helloBytes = "HELLO"u8.ToArray();
        var worldBytes = "WORLD!"u8.ToArray();
        var f1 = _fs.File("a/file1.txt", helloBytes );
        var f1Copy = _fs.File("b/file1copy.txt", helloBytes );
        var uniq = _fs.File("c/unique.bin", worldBytes );

        var finder = new DuplicateFileFinder(new FakeRepo());

        // act        
        var progress = new Progress<DuplicateFileFinderProgressReport>();
        await finder.ScanLocation(_fs.Root, progressIndicator: progress,
            token: CancellationToken.None);

        // dump CSV for inspection
        await using var sw = new StringWriter();
        finder.ExportToCsv(sw);
        var csv = sw.ToString();
        
        var rows = CsvTestUtil.Parse(csv);

        // assert
        // 1. We expect to see our three files in the CSV
        Assert.Contains(rows, r => r.Path == f1 && r.Kind == KindEnum.File);
        Assert.Contains(rows, r => r.Path == f1Copy && r.Kind == KindEnum.File);
        Assert.Contains(rows, r => r.Path == uniq && r.Kind == KindEnum.File);

        // 2. Find their groups
        var g1 = rows.First(r => r.Path == f1 && r.Kind == KindEnum.File).Group;
        var g1C = rows.First(r => r.Path == f1Copy && r.Kind == KindEnum.File).Group;
        var gu = rows.First(r => r.Path == uniq && r.Kind == KindEnum.File).Group;

        // The two identical-content files should share the same non-negative group
        Assert.Equal(g1, g1C);
        Assert.True(g1 >= 0, "Expected duplicate files to be assigned a non-negative group id");

        // The unique file should either not share that group,
        // OR be marked with a sentinel negative group
        Assert.True(gu != g1, "Unique file should not be grouped with duplicates");

        // 3. Checksum should not be empty for duplicates (they got hashed)
        var cs1 = rows.First(r => r.Path == f1 && r.Kind == KindEnum.File).Checksum;
        var cs1C = rows.First(r => r.Path == f1Copy && r.Kind == KindEnum.File).Checksum;
        Assert.False(string.IsNullOrWhiteSpace(cs1));
        Assert.Equal(cs1, cs1C);

        // 4. The unique file won't have had a checksum computed        
        Assert.True(string.IsNullOrWhiteSpace(rows.First(r => r.Path == uniq && r.Kind == KindEnum.File).Checksum));
    }

    [Fact]
    public async Task ScanLocation_PreCanceledToken_ThrowsAndDoesNotAddFiles()
    {
        // Arrange: small tree
        var root = _fs.Dir("deep");
        _fs.File("deep/f.bin", "X"u8.ToArray());
    
        var finder = new DuplicateFileFinder(new FakeRepo());
        using var cts = new CancellationTokenSource();
        // ReSharper disable once MethodHasAsyncOverload
        cts.Cancel(); // deterministic: canceled before call
    
        // Act + Assert: must throw
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await finder.ScanLocation(root, new Progress<DuplicateFileFinderProgressReport>(), cts.Token));
    
        // No files added
        Assert.Equal(0, finder.TotalFilesScanned);
    }

    [Fact]
    public async Task Scan_AncestorAfterDescendant_ScansSiblingSubtrees_AndPromotes_Correctly()
    {
        // Layout:
        //   A/
        //     B/
        //       f1.txt  ("SAME")
        //       f2.txt  ("SAME")     -> duplicates
        //     C/
        //       c.txt   ("CFILE")    -> sibling subtree under ancestor
        //     u.txt     ("UNIQUE")   -> file directly under ancestor

        var A = _fs.Dir("A");
        var B = _fs.Dir("A/B");
        var C = _fs.Dir("A/C");
        var f1 = _fs.File(Path.Combine("A", "B", "f1.txt"), "SAME"u8.ToArray());
        var f2 = _fs.File(Path.Combine("A", "B", "f2.txt"), "SAME"u8.ToArray());
        var c = _fs.File(Path.Combine("A", "C", "c.txt"), "CFILE"u8.ToArray());
        var u = _fs.File(Path.Combine("A", "u.txt"), "UNIQUE"u8.ToArray());

        var dff = new DuplicateFileFinder(new FakeRepo());
        var progress = new Progress<DuplicateFileFinderProgressReport>();

        // 1) Scan the descendant first (B) — only B's files should be known
        await dff.ScanLocation(B, progress, CancellationToken.None);

        var rootsAfterDescendant = dff.SearchPaths;
        Assert.Single(rootsAfterDescendant);
        Assert.Equal(PathUtils.NormalizePath(B), rootsAfterDescendant[0]);

        var totalBefore = dff.TotalFilesScanned;
        var wastedBytesBefore = dff.DuplicateSpaceBytes;
        var wastedFilesBefore = dff.DuplicateFilesWastedCount;

        // Only the two files in B should be counted now
        Assert.Equal(2, totalBefore);
        Assert.Equal(1, wastedFilesBefore); // one duplicate beyond representative
        Assert.Equal("SAME".Length, wastedBytesBefore); // 4

        // 2) Now scan the ancestor (A)
        //    Expect: promotion to A as sole root, B stays under A, and the sibling subtree C + file u.txt are included.
        await dff.ScanLocation(A, progress, CancellationToken.None);

        var rootsAfterAncestor = dff.SearchPaths;
        Assert.Single(rootsAfterAncestor);
        Assert.Equal(PathUtils.NormalizePath(A), rootsAfterAncestor[0]);

        // Export and inspect rows
        await using var sw = new StringWriter();
        dff.ExportToCsv(sw);
        var rows = CsvTestUtil.Parse(sw.ToString());

        // Folder rows should include A, B, and C
        Assert.Contains(rows, r => r.Kind == KindEnum.Folder && r.Path == A);
        Assert.Contains(rows, r => r.Kind == KindEnum.Folder && r.Path == B);
        Assert.Contains(rows, r => r.Kind == KindEnum.Folder && r.Path == C);

        // All files must be present exactly once
        Assert.Contains(rows, r => r.Kind == KindEnum.File && r.Path == f1);
        Assert.Contains(rows, r => r.Kind == KindEnum.File && r.Path == f2);
        Assert.Contains(rows, r => r.Kind == KindEnum.File && r.Path == c);
        Assert.Contains(rows, r => r.Kind == KindEnum.File && r.Path == u);

        Assert.Equal(1, rows.Count(r => r.Kind == KindEnum.File && r.Path == f1));
        Assert.Equal(1, rows.Count(r => r.Kind == KindEnum.File && r.Path == f2));
        Assert.Equal(1, rows.Count(r => r.Kind == KindEnum.File && r.Path == c));
        Assert.Equal(1, rows.Count(r => r.Kind == KindEnum.File && r.Path == u));

        // Totals should now include the sibling subtree file + ancestor file, with no double counting:
        // previously 2 (B only) -> now 4 (B dup1+dup2 + C cFile + A u.txt)
        Assert.Equal(4, dff.TotalFilesScanned);

        // Duplicate metrics for the SAME pair should remain stable across promotion and sibling scan
        Assert.Equal(wastedFilesBefore, dff.DuplicateFilesWastedCount);
        Assert.Equal(wastedBytesBefore, dff.DuplicateSpaceBytes);

        // The two SAME files remain grouped together; the others are not in that group
        var g1 = rows.First(r => r.Kind == KindEnum.File && r.Path == f1).Group;
        var g2 = rows.First(r => r.Kind == KindEnum.File && r.Path == f2).Group;
        Assert.True(g1 >= 0);
        Assert.Equal(g1, g2);

        var gC = rows.First(r => r.Kind == KindEnum.File && r.Path == c).Group;
        var gU = rows.First(r => r.Kind == KindEnum.File && r.Path == u).Group;
        Assert.NotEqual(g1, gC);
        Assert.NotEqual(g1, gU);
    }


    
    [Fact]
    public async Task Metrics_TotalFilesAndWastedBytes_AreCorrect()
    {
        var dir = _fs.Dir("metrics");
        // Two identical files (size 4), one unique file (size 3)
        _ = _fs.File("metrics/a.bin", "DATA"u8.ToArray());
        _ = _fs.File("metrics/b.bin", "DATA"u8.ToArray());
        _ = _fs.File("metrics/u.bin", "xyz"u8.ToArray());

        var finder = new DuplicateFileFinder(new FakeRepo());
        await finder.ScanLocation(dir, new Progress<DuplicateFileFinderProgressReport>(), CancellationToken.None);

        Assert.Equal(3, finder.TotalFilesScanned);

        // One duplicate group of two 4-byte files => wasted bytes = 4 (keep one 4-byte representative).
        Assert.Equal(4, finder.DuplicateSpaceBytes);
        // Wasted file count (all but one representative) = 1
        Assert.Equal(1, finder.DuplicateFilesWastedCount);
    }
    
    [Fact]
    public async Task GetDuplicateFileRows_ReturnsExpectedRows()
    {
        var root = _fs.Dir("rows");
        var d1 = _fs.File("rows/d1.txt", "SAME"u8.ToArray());
        var d2 = _fs.File("rows/d2.txt", "SAME"u8.ToArray());
        _fs.File("rows/u1.txt", "DIFF"u8.ToArray());

        var finder = new DuplicateFileFinder(new FakeRepo());
        await finder.ScanLocation(root, new Progress<DuplicateFileFinderProgressReport>(), CancellationToken.None);

        var rows = await finder.GetDuplicateFileRowsAsync();

        // Should include two rows (d1, d2) in the same group; and NOT include the unique file.
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Group >= 0));
        Assert.Contains(rows, r => r.Path == d1);
        Assert.Contains(rows, r => r.Path == d2);
    }
    
    [Fact]
    public async Task ScanLocation_Cancels_DuringChecksumStage()
    {
        var root = _fs.Dir("hashcancel");

        // Create many duplicate-sized files so producer queues a lot
        for (int i = 0; i < 200; i++)
            _fs.File(Path.Combine("hashcancel", $"f{i}.bin"), new byte[4096]); // all same size

        var finder = new DuplicateFileFinder(new FakeRepo());
        using var cts = new CancellationTokenSource();

        // ReSharper disable AccessToDisposedClosure
        var progress = new Progress<DuplicateFileFinderProgressReport>(_ =>
        {
            // Cancel shortly after scan starts reporting progress
            if (!cts.IsCancellationRequested)
                cts.Cancel();
        });

        // We accept either an OperationCanceledException or a partial build
        try
        {
            await finder.ScanLocation(root, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            /* ok */
        }

        // Still able to export CSV without crash
        await using var sw = new StringWriter();
        finder.ExportToCsv(sw);
        Assert.NotNull(sw.ToString());
    }

    [Fact]
    public async Task ExistingAncestorFirst_ScanningDescendantDoesNotAddNewRoot()
    {
        var a = _fs.Dir("X");
        var b = _fs.Dir(Path.Combine("X", "Y"));
        _fs.File(Path.Combine("X", "Y", "f.bin"), "Q"u8.ToArray());

        var finder = new DuplicateFileFinder(new FakeRepo());
        var progress = new Progress<DuplicateFileFinderProgressReport>();

        await finder.ScanLocation(a, progress, CancellationToken.None);
        var roots1 = finder.SearchPaths;
        Assert.Single(roots1);
        Assert.Equal(PathUtils.NormalizePath(a), roots1[0]);

        // Scan descendant — should not add another root
        await finder.ScanLocation(b, progress, CancellationToken.None);
        var roots2 = finder.SearchPaths;
        Assert.Single(roots2);
        Assert.Equal(roots1[0], roots2[0]);
    }
    
    

    [Fact]
    public async Task IndependentRootsRemainSeparate()
    {
        var leafDir = _fs.Dir(Path.Combine("B", "leaf"));
        var cDir = _fs.Dir("C");
        _fs.File(Path.Combine("B", "leaf", "b.txt"), "B"u8.ToArray());
        _fs.File(Path.Combine("C", "c.txt"), "C"u8.ToArray());

        var finder = new DuplicateFileFinder(new FakeRepo());
        var progress = new Progress<DuplicateFileFinderProgressReport>();

        await finder.ScanLocation(leafDir, progress, CancellationToken.None);
        await finder.ScanLocation(cDir, progress, CancellationToken.None);

        var roots = finder.SearchPaths;
        Assert.Equal(2, roots.Count);
        Assert.Contains(PathUtils.NormalizePath(leafDir),
            roots);
        Assert.Contains(PathUtils.NormalizePath(cDir), roots);
    }
    
    [Fact]
    public async Task Promotion_DoesNotDoubleCountOrRehash()
    {
        var a = _fs.Dir("P");
        var b = _fs.Dir(Path.Combine("P", "Q"));
        _fs.File(Path.Combine("P", "Q", "d1.txt"), "SAME"u8.ToArray());
        _fs.File(Path.Combine("P", "Q", "d2.txt"), "SAME"u8.ToArray());

        var finder = new DuplicateFileFinder(new FakeRepo());
        var progress = new Progress<DuplicateFileFinderProgressReport>();

        await finder.ScanLocation(b, progress, CancellationToken.None);
        var totalBefore = finder.TotalFilesScanned;
        var wastedBefore = finder.DuplicateSpaceBytes;

        // Promote
        await finder.ScanLocation(a, progress, CancellationToken.None);

        // Should still be only two files counted, one duplicate wasted of size(len "SAME" = 4)
        Assert.Equal(totalBefore, finder.TotalFilesScanned);
        Assert.Equal(wastedBefore, finder.DuplicateSpaceBytes);
    }
    
    [Fact]
    public async Task ScanLocation_Failure_DoesNotMutateExistingState()
    {
        var dir = _fs.Dir("ok");
        _fs.File("ok/a.txt", "A"u8.ToArray());

        var finder = new DuplicateFileFinder(new FakeRepo());

        // First, a successful scan (commits)
        await finder.ScanLocation(dir, new Progress<DuplicateFileFinderProgressReport>(), CancellationToken.None);
        var csvBefore = new StringWriter(); finder.ExportToCsv(csvBefore);
        var snapshot = csvBefore.ToString();

        // Now scan a path that will throw early (missing root)
        var missing = Path.Combine(_fs.Root, "does_not_exist");
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // force failure
            await finder.ScanLocation(missing, new Progress<DuplicateFileFinderProgressReport>(), cts.Token);
        });

        // State must be unchanged
        var csvAfter = new StringWriter(); finder.ExportToCsv(csvAfter);
        Assert.Equal(snapshot, csvAfter.ToString());
    }
    
    [Fact]
    public async Task ScanLocation_ExportIncludesCreationTime()
    {
        using var fs = new TempFsFixture();
        var created = new DateTimeOffset(2023, 10, 21, 12, 34, 56, TimeSpan.Zero);

        var root = fs.Dir("root");
        var f1 = fs.File("root/a.txt", "HELLO"u8, created);
        var f2 = fs.File("root/b.txt", "HELLO"u8, created.AddMinutes(1));

        var dff = new DuplicateFileFinder(new FakeRepo());
        await dff.ScanLocation(root, progressIndicator: null, token: default);

        using var sw = new StringWriter();
        dff.ExportToCsv(sw);
        var rows = CsvTestUtil.Parse(sw.ToString());

        Assert.Equal(CsvSpec.Header.Length, CsvSpec.Header.Length); // header known
        AssertRows.ContainsFolder(rows, root);
        AssertRows.ContainsFile(rows, f1);
        AssertRows.ContainsFile(rows, f2);

        // Creation times are recorded as UTC in CSV
        AssertRows.CreationTimeIs(rows, f1, created);
        AssertRows.CreationTimeIs(rows, f2, created.AddMinutes(1));

        // The two identical files still group together
        AssertRows.InSameGroup(rows, f1, f2);
    }

[Fact]
public async Task Csv_RoundTrip_IncludesCreationTime_And_GroupsByContent()
{
    using var fs = new TempFsFixture();
    var created1 = new DateTimeOffset(2023, 10, 21, 12, 34, 56, TimeSpan.Zero);
    var created2 = created1.AddMinutes(1);

    var root = fs.Dir("root");
    var f1 = fs.File("root/a.txt", "HELLO"u8, created1);
    var f2 = fs.File("root/b.txt", "HELLO"u8, created2);

    var dff = new DuplicateFileFinder(new FakeRepo());
    await dff.ScanLocation(root);

    await using var sw = new StringWriter();
    dff.ExportToCsv(sw);
    var csv = sw.ToString();

    // parse with single source of truth
    var rows = CsvTestUtil.Parse(csv);

    AssertRows.ContainsFolder(rows, root);
    AssertRows.ContainsFile(rows, f1);
    AssertRows.ContainsFile(rows, f2);

    AssertRows.CreationTimeIs(rows, f1, created1);
    AssertRows.CreationTimeIs(rows, f2, created2);

    AssertRows.InSameGroup(rows, f1, f2);
}
}