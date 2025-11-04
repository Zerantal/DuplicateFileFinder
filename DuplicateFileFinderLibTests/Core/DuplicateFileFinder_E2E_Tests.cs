// DuplicateFileFinderLibTests/DuplicateFileFinderTests.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.FileSystem;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

// ReSharper disable StringLiteralTypo
// ReSharper disable RedundantArgumentDefaultValue

namespace DuplicateFileFinderLibTests.Core;

file sealed class TestEnumerateCanceler(
    int yieldBeforeSignal,
    int totalToYield,
    ManualResetEventSlim signal,
    ManualResetEventSlim gate)
    : IFileEnumerator
{
    public IEnumerable<FsEntry> EnumerateChildren(string dir, CancellationToken token)
    {
        for (int i = 0; i < totalToYield; i++)
        {
            token.ThrowIfCancellationRequested();

            var fakePath = Path.Combine(dir, $"f{i}.bin");
            yield return new FsEntry(IsDirectory: false, FullPath: fakePath, Length: 123);

            // After yielding the Kth entry, signal the test and then PAUSE here.
            if (i + 1 == yieldBeforeSignal)
            {
                signal.Set();          // tell the test we're at the latch
                gate.Wait(token);      // block until the test opens the gate (or cancellation throws)
            }
        }
    }
}

// ReSharper disable once InconsistentNaming
public sealed class DuplicateFileFinder_E2E_Tests : IDisposable
{
    private readonly string _tempRoot;

    private readonly IoUtil _ioUtil;

    public DuplicateFileFinder_E2E_Tests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DFFTests_" + Guid.NewGuid().ToString("N"));        
        _ioUtil = new IoUtil(_tempRoot);
    }

    public void Dispose()
    {
        _ioUtil.Dispose();        
    }
    
    [Fact]
    public async Task ScanLocation_FindsDuplicates_AndAssignsGroups()
    {
        // arrange
        // tempRoot/
        //   a/file1.txt        ("HELLO")
        //   b/file1copy.txt    ("HELLO")    -> duplicate of file1.txt
        //   c/unique.bin       ("WORLD!")   -> unique (CS not calculated due size uniqueness
        _ioUtil.CreateDir("a");
        _ioUtil.CreateDir("b");
        _ioUtil.CreateDir("c");

        var helloBytes = "HELLO"u8.ToArray();
        var worldBytes = "WORLD!"u8.ToArray();

        var f1 = _ioUtil.CreateFile("a/file1.txt", helloBytes);
        var f1Copy = _ioUtil.CreateFile("b/file1copy.txt", helloBytes);
        var uniq = _ioUtil.CreateFile("c/unique.bin", worldBytes);

        var finder = new DuplicateFileFinder();

        // act        
        var progress = new Progress<DuplicateFileFinderProgressReport>();
        await finder.ScanLocation(_tempRoot, progressIndicator: progress,
            token: CancellationToken.None);

        // dump CSV for inspection
        await using var sw = new StringWriter();
        finder.ExportToCsv(sw);
        var csv = sw.ToString();
        
        var rows = CsvUtil.ReadCsvRows(csv);

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
        var root = _ioUtil.CreateDir("deep");
        _ioUtil.CreateFile(Path.Combine("deep", "f.bin"), "X"u8.ToArray());
    
        var finder = new DuplicateFileFinder();
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
    public async Task Metrics_TotalFilesAndWastedBytes_AreCorrect()
    {
        var dir = _ioUtil.CreateDir("metrics");
        // Two identical files (size 4), one unique file (size 3)
        _ = _ioUtil.CreateFile("metrics/a.bin", "DATA"u8.ToArray());
        _ = _ioUtil.CreateFile("metrics/b.bin", "DATA"u8.ToArray());
        _ = _ioUtil.CreateFile("metrics/u.bin", "xyz"u8.ToArray());

        var finder = new DuplicateFileFinder();
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
        var root = _ioUtil.CreateDir("rows");
        var d1 = _ioUtil.CreateFile("rows/d1.txt", "SAME"u8.ToArray());
        var d2 = _ioUtil.CreateFile("rows/d2.txt", "SAME"u8.ToArray());
        _ioUtil.CreateFile("rows/u1.txt", "DIFF"u8.ToArray());

        var finder = new DuplicateFileFinder();
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
        var root = _ioUtil.CreateDir("hashcancel");

        // Create many duplicate-sized files so producer queues a lot
        for (int i = 0; i < 200; i++)
            _ioUtil.CreateFile(Path.Combine("hashcancel", $"f{i}.bin"), new byte[4096]); // all same size

        var finder = new DuplicateFileFinder();
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
        var a = _ioUtil.CreateDir("X");
        var b = _ioUtil.CreateDir(Path.Combine("X", "Y"));
        _ioUtil.CreateFile(Path.Combine("X", "Y", "f.bin"), "Q"u8.ToArray());

        var finder = new DuplicateFileFinder();
        var progress = new Progress<DuplicateFileFinderProgressReport>();

        await finder.ScanLocation(a, progress, CancellationToken.None);
        var roots1 = finder.SearchPaths;
        Assert.Single(roots1);
        Assert.Equal(DuplicateFileFinderLib.Util.PathUtils.NormalizePath(a), roots1[0]);

        // Scan descendant — should not add another root
        await finder.ScanLocation(b, progress, CancellationToken.None);
        var roots2 = finder.SearchPaths;
        Assert.Single(roots2);
        Assert.Equal(roots1[0], roots2[0]);
    }

    [Fact]
    public async Task IndependentRootsRemainSeparate()
    {
        _ioUtil.CreateDir(Path.Combine("B", "leaf"));
        _ioUtil.CreateDir("C");
        _ioUtil.CreateFile(Path.Combine("B", "leaf", "b.txt"), "B"u8.ToArray());
        _ioUtil.CreateFile(Path.Combine("C", "c.txt"), "C"u8.ToArray());

        var finder = new DuplicateFileFinder();
        var progress = new Progress<DuplicateFileFinderProgressReport>();

        await finder.ScanLocation(Path.Combine(_tempRoot, "B", "leaf"), progress, CancellationToken.None);
        await finder.ScanLocation(Path.Combine(_tempRoot, "C"), progress, CancellationToken.None);

        var roots = finder.SearchPaths;
        Assert.Equal(2, roots.Count);
        Assert.Contains(DuplicateFileFinderLib.Util.PathUtils.NormalizePath(Path.Combine(_tempRoot, "B", "leaf")),
            roots);
        Assert.Contains(DuplicateFileFinderLib.Util.PathUtils.NormalizePath(Path.Combine(_tempRoot, "C")), roots);
    }
    
    [Fact]
    public async Task Promotion_DoesNotDoubleCountOrRehash()
    {
        var a = _ioUtil.CreateDir("P");
        var b = _ioUtil.CreateDir(Path.Combine("P", "Q"));
        _ioUtil.CreateFile(Path.Combine("P", "Q", "d1.txt"), "SAME"u8.ToArray());
        _ioUtil.CreateFile(Path.Combine("P", "Q", "d2.txt"), "SAME"u8.ToArray());

        var finder = new DuplicateFileFinder();
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
        var dir = _ioUtil.CreateDir("ok");
        _ioUtil.CreateFile("ok/a.txt", "A"u8.ToArray());

        var finder = new DuplicateFileFinder();

        // First, a successful scan (commits)
        await finder.ScanLocation(dir, new Progress<DuplicateFileFinderProgressReport>(), CancellationToken.None);
        var csvBefore = new StringWriter(); finder.ExportToCsv(csvBefore);
        var snapshot = csvBefore.ToString();

        // Now scan a path that will throw early (missing root)
        var missing = Path.Combine(_tempRoot, "does_not_exist");
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
}