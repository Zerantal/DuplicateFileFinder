// DuplicateFileFinderLibTests/Core/DuplicateFileFinder_Repo_Tests.cs

using System;
using System.Collections.Generic;
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

namespace DuplicateFileFinderLibTests.Core;

// NOTE: These tests deliberately do NOT exercise the legacy _root / CSV / metrics APIs.
// They focus only on Repo-facing behavior of DuplicateFileFinder.
public sealed class DuplicateFileFinder_Repo_Tests : IDisposable
{
    private readonly TempFsFixture _fs = new();

    public void Dispose()
    {
        _fs.Dispose();
    }

    // ----------------- Support Types -----------------

    private sealed class CapturingRepo : IRepo
    {
        public readonly List<string> BeginScanRoots = new();
        public readonly List<CapturingSession> Sessions = new();
        public CapturingSession? LastSession { get; private set; }
        public bool CompactIfNeededCalled { get; private set; }

        public RepoViewSnapshot GetSnapshot()
            => throw new NotImplementedException("Snapshot not used in these tests.");

        public IReadOnlyList<ScanRun> ScanRunsView { get; } = Array.Empty<ScanRun>();

        public IScanSession BeginScan(string rootPath, int maxFilesBeforeFlush = 10_000, int maxDirsBeforeFlush = 1_000)
        {
            var session = new CapturingSession(rootPath);
            LastSession = session;
            Sessions.Add(session);
            BeginScanRoots.Add(rootPath);
            return session;
        }

        public void CommitDelta(RepoDelta delta) { }

        public Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void SaveSnapshot() { }

        public void CompactIfNeeded(RepoCompactionPolicy? policy = null)
            => CompactIfNeededCalled = true;

        public void CompactNow() { }

        public string GetFullDirPath(Guid dirId)
            => throw new NotImplementedException();
    }

    private sealed class CapturingSession : IScanSession
    {
        public ScanRun Run { get; }
        public long ScanSequence => Run.ScanSequence;
        public string RootPath => Run.RootPath;

        public readonly List<(string FullPath, ScanEntryStatus Status, string? Error)> ObservedDirectories = new();
        public readonly List<ObservedFile> ObservedFiles = new();

        public int FlushCallCount { get; private set; }
        public int CompleteCallCount { get; private set; }
        public List<(string? Error, bool Cancelled)> FailCalls { get; } = new();
        public int DisposeCallCount { get; private set; }

        public CapturingSession(string rootPath)
        {
            Run = new ScanRun
            {
                ScanSequence = 1,
                RootPath = rootPath,
                StartedAt = DateTimeOffset.UtcNow,
                Status = ScanRunStatus.InProgress
            };
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }

        public Guid AddOrUpdateDirectory(string fullPath, ScanEntryStatus? status = null, string? errorMessage = null)
        {
            ObserveDirectory(fullPath, status ?? ScanEntryStatus.Enumerated, errorMessage);
            return Guid.NewGuid();
        }

        public void AddOrUpdateFile(string fullFilePath, long? size = null, HashKey? hash = null, DateTimeOffset? modified = null,
            DateTimeOffset? created = null, ScanEntryStatus? status = null, string? errorMessage = null)
        {
            var existingFile = ObservedFiles.LastOrDefault(f => f.FullPath == fullFilePath);
            size ??= existingFile?.Size ?? 0;
            created ??= existingFile?.Created ?? default(DateTimeOffset);
            ObserveFile(
                fullFilePath, 
                size.Value,
                hash ?? HashKey.NotComputed, 
                modified ?? default(DateTimeOffset),
                created.Value,
                status ?? ScanEntryStatus.Enumerated,
                errorMessage);
        }

        public Guid ObserveDirectory(string fullPath, ScanEntryStatus status, string? errorMessage = null)
        {
            ObservedDirectories.Add((fullPath, status, errorMessage));
            return Guid.NewGuid();
        }

        public void ObserveFile(
            string fullFilePath,
            long size,
            HashKey hash,
            DateTimeOffset modified,
            DateTimeOffset created,
            ScanEntryStatus status,
            string? errorMessage = null)
        {
            ObservedFiles.Add(new ObservedFile(
                fullFilePath, size, hash, modified, created, status, errorMessage));
        }

        public Task FlushProgressAsync(CancellationToken cancellationToken = default)
        {
            FlushCallCount++;
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            CompleteCallCount++;
            return Task.CompletedTask;
        }

        public Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default)
        {
            FailCalls.Add((errorMessage, cancelled));
            return Task.CompletedTask;
        }
    }

    private sealed class ObservedFile
    {
        public string FullPath { get; }
        public long Size { get; }
        public HashKey Hash { get; }
        public DateTimeOffset Modified { get; }
        public DateTimeOffset Created { get; }
        public ScanEntryStatus Status { get; }
        public string? ErrorMessage { get; }

        public ObservedFile(
            string fullPath,
            long size,
            HashKey hash,
            DateTimeOffset modified,
            DateTimeOffset created,
            ScanEntryStatus status,
            string? errorMessage)
        {
            FullPath = fullPath;
            Size = size;
            Hash = hash;
            Modified = modified;
            Created = created;
            Status = status;
            ErrorMessage = errorMessage;
        }
    }
    
    // ----------------- Tests -----------------

    [Fact]
    public async Task ScanLocation_PopulatesRepo_WithDirectoriesAndHashedFiles()
    {
        // Arrange:
        // temp/
        //   a/file1.txt        ("HELLO")
        //   b/file1copy.txt    ("HELLO") -> duplicate, same size & content
        //   c/unique.bin       ("WORLD") -> unique size, should not be hashed
        _fs.Dir("a");
        _fs.Dir("b");
        _fs.Dir("c");

        var helloBytes = "HELLO"u8.ToArray();
        var worldBytes = "WORLD"u8.ToArray();

        var f1 = _fs.File("a/file1.txt", helloBytes);
        var f1Copy = _fs.File("b/file1copy.txt", helloBytes);
        var uniq = _fs.File("c/unique.bin", worldBytes);

        var repo = new CapturingRepo();
        var finder = new DuplicateFileFinder(repo);

        // Act
        var progress = new Progress<DuplicateFileFinderProgressReport>();
        await finder.ScanLocationAsync(_fs.Root, progress);

        var session = Assert.IsType<CapturingSession>(repo.LastSession);

        // BeginScan root path is normalized
        Assert.Single(repo.BeginScanRoots);

        // Repo should see the root and the subdirectories as ObservedDirectories
        var observedDirPaths = session.ObservedDirectories
            .Select(d => PathUtils.NormalizePath(d.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains(PathUtils.NormalizePath(_fs.Root), observedDirPaths);
        Assert.Contains(PathUtils.NormalizePath(Path.Combine(_fs.Root, "a")), observedDirPaths);
        Assert.Contains(PathUtils.NormalizePath(Path.Combine(_fs.Root, "b")), observedDirPaths);
        Assert.Contains(PathUtils.NormalizePath(Path.Combine(_fs.Root, "c")), observedDirPaths);

        // All observed directories should be marked Enumerated
        Assert.All(session.ObservedDirectories,
            d => Assert.Equal(ScanEntryStatus.Enumerated, d.Status));

        // All three files are observed and hashed
        var observedFiles = session.ObservedFiles.Where(f => f.Status == ScanEntryStatus.Hashed).ToList();
        Assert.Equal(3, observedFiles.Count());

        var f1Obs = Assert.Single(observedFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(f1));
        var f1CopyObs = Assert.Single(observedFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(f1Copy));

        Assert.Equal(f1Obs.Hash, f1CopyObs.Hash);

        var uniqObs = observedFiles
            .SingleOrDefault(f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(uniq));
        if (uniqObs is not null)
            Assert.NotEqual(f1Obs.Hash, uniqObs.Hash);

        Assert.All(observedFiles, f =>
        {
            Assert.Equal(ScanEntryStatus.Hashed, f.Status);
        });

        // Successful scan should call CompleteAsync and CompactIfNeeded
        Assert.Equal(1, session.CompleteCallCount);
        Assert.Empty(session.FailCalls);
        Assert.Equal(1, session.DisposeCallCount);
        Assert.True(repo.CompactIfNeededCalled);
    }

    [Fact]
    public async Task ScanLocation_ObserveFile_PopulatesSizesAndTimestamps()
    {
        var root = _fs.Dir("root");
        var created1 = new DateTimeOffset(2023, 10, 21, 12, 34, 56, TimeSpan.Zero);
        var created2 = created1.AddMinutes(1);

        var f1 = _fs.File("root/a.txt", "HELLO"u8, created1);
        var f2 = _fs.File("root/b.txt", "WORLD"u8, created2);

        var repo = new CapturingRepo();
        var finder = new DuplicateFileFinder(repo);

        await finder.ScanLocationAsync(root, new Progress<DuplicateFileFinderProgressReport>());

        var session = Assert.IsType<CapturingSession>(repo.LastSession);

        var f1Obs = Assert.Single(session.ObservedFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(f1) && f.Status == ScanEntryStatus.Hashed);
        var f2Obs = Assert.Single(session.ObservedFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(f2) && f.Status == ScanEntryStatus.Hashed);

        Assert.Equal(new FileInfo(f1).Length, f1Obs.Size);
        Assert.Equal(new FileInfo(f2).Length, f2Obs.Size);

        Assert.Equal(created1, f1Obs.Created);
        Assert.Equal(created2, f2Obs.Created);
        
        Assert.Equal(ScanEntryStatus.Hashed, f1Obs.Status);
        Assert.Equal(ScanEntryStatus.Hashed, f2Obs.Status);
    }

    [Fact]
    public async Task ScanLocation_ProgressReports_PhasesAndFinalCompletion()
    {
        var root = _fs.Dir("root");
        _fs.File("root/file.bin", "DATA"u8.ToArray());

        var repo = new CapturingRepo();
        var finder = new DuplicateFileFinder(repo, false);

        var reports = new List<DuplicateFileFinderProgressReport>();
        var progress = new Progress<DuplicateFileFinderProgressReport>(r => reports.Add(r));

        await finder.ScanLocationAsync(root, progress);

        Assert.NotEmpty(reports);

        var last = reports[^1];
        Assert.Equal(ScanPhase.Completed, last.Phase);
        Assert.False(last.IsRunning);
        Assert.Equal(1.0, last.PercentComplete, 5e-3);

        Assert.Contains(reports, r => r.Phase == ScanPhase.Enumerating);
        var lastEnumIdx = reports.FindLastIndex(r => r.Phase == ScanPhase.Enumerating);
        Assert.Contains(reports[(lastEnumIdx + 1)..], r => r.Phase == ScanPhase.Hashing);
    }

    [Fact]
    public async Task ScanLocation_MultipleCalls_BeginScanCalledPerScan()
    {
        var root1 = _fs.Dir("root1");
        var root2 = _fs.Dir("root2");
        _fs.File("root1/a.bin", "A"u8);
        _fs.File("root2/b.bin", "B"u8);

        var repo = new CapturingRepo();
        var finder = new DuplicateFileFinder(repo);
        var progress = new Progress<DuplicateFileFinderProgressReport>();

        await finder.ScanLocationAsync(root1, progress);
        await finder.ScanLocationAsync(root2, progress);

        Assert.Equal(2, repo.BeginScanRoots.Count);

        Assert.Equal(
            PathUtils.NormalizePath(root1),
            PathUtils.NormalizePath(repo.BeginScanRoots[0]));
        Assert.Equal(
            PathUtils.NormalizePath(root2),
            PathUtils.NormalizePath(repo.BeginScanRoots[1]));

        Assert.Equal(2, repo.Sessions.Count);
        Assert.All(repo.Sessions, s =>
        {
            Assert.Equal(1, s.CompleteCallCount);
            Assert.Empty(s.FailCalls);
            Assert.Equal(1, s.DisposeCallCount);
        });
    }

    [Fact]
    public async Task ScanLocation_PreCanceledToken_CallsFailAsyncWithCancelledTrue_AndDoesNotComplete()
    {
        // Arrange
        var root = _fs.Dir("root");
        _fs.File("root/f.bin", "X"u8.ToArray());

        var repo = new CapturingRepo();
        var finder = new DuplicateFileFinder(repo);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // canceled before the scan starts

        // Act + Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await finder.ScanLocationAsync(root, new Progress<DuplicateFileFinderProgressReport>(), cts.Token));

        var session = Assert.IsType<CapturingSession>(repo.LastSession);

        Assert.Equal(0, session.CompleteCallCount);
        Assert.Single(session.FailCalls);

        var fail = session.FailCalls[0];
        Assert.True(fail.Cancelled);
        Assert.False(string.IsNullOrWhiteSpace(fail.Error));
        Assert.Equal(1, session.DisposeCallCount);
    }

    [Fact]
    public async Task ScanLocation_ExceptionDuringScan_CallsFailAsyncWithCancelledFalse_AndDoesNotComplete()
    {
        // Arrange: use a missing directory so the enumerator will fail
        var missingRoot = Path.Combine(_fs.Root, "does_not_exist");

        var repo = new CapturingRepo();
        var finder = new DuplicateFileFinder(repo);

        // Act: expect some exception (e.g. DirectoryNotFoundException)
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
            await finder.ScanLocationAsync(missingRoot, null, CancellationToken.None));


        var session = Assert.IsType<CapturingSession>(repo.LastSession);

        // Assert: fail recorded with cancelled = false, no CompleteAsync
        Assert.Equal(0, session.CompleteCallCount);
        Assert.Single(session.FailCalls);

        var fail = session.FailCalls[0];
        Assert.False(fail.Cancelled);
        // Error message may vary; just ensure something was recorded
        Assert.False(string.IsNullOrWhiteSpace(fail.Error));
        Assert.Equal(1, session.DisposeCallCount);
    }

    [Fact]
    public async Task ScanLocation_CancellationDuringEnumeration_SetsFailCancelledTrue()
    {
        var root = _fs.Dir("enumcancel");
        _fs.Dir(Path.Combine("enumcancel", "sub1"));
        _fs.Dir(Path.Combine("enumcancel", "sub2"));
        _fs.File(Path.Combine("enumcancel", "sub1", "a.bin"), new byte[16]);
        _fs.File(Path.Combine("enumcancel", "sub2", "b.bin"), new byte[16]);

        var repo = new CapturingRepo();
        var finder = new DuplicateFileFinder(repo);

        using var cts = new CancellationTokenSource();
        var reports = new List<DuplicateFileFinderProgressReport>();

        var progress = new Progress<DuplicateFileFinderProgressReport>(r =>
        {
            reports.Add(r);
            if (r.Phase == ScanPhase.Enumerating &&
                r.Processed >= 1 &&
                !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await finder.ScanLocationAsync(root, progress, cts.Token));

        var session = Assert.IsType<CapturingSession>(repo.LastSession);

        Assert.Equal(0, session.CompleteCallCount);
        Assert.Single(session.FailCalls);
        Assert.True(session.FailCalls[0].Cancelled);
        Assert.False(string.IsNullOrWhiteSpace(session.FailCalls[0].Error));
        Assert.Equal(1, session.DisposeCallCount);

        Assert.Contains(reports, r => r.Phase == ScanPhase.Enumerating);
    }

    [Fact]
    public async Task ScanLocation_CancellationDuringHashing_SetsFailCancelledTrue()
    {
        var root = _fs.Dir("hashcancel");
        for (var i = 0; i < 50; i++)
            _fs.File(Path.Combine("hashcancel", $"f{i}.bin"), new byte[4096]);

        var repo = new CapturingRepo();
        var finder = new DuplicateFileFinder(repo);

        using var cts = new CancellationTokenSource();
        var reports = new List<DuplicateFileFinderProgressReport>();

        var progress = new Progress<DuplicateFileFinderProgressReport>(r =>
        {
            reports.Add(r);
            if (r.Phase == ScanPhase.Hashing &&
                r.Processed > 0 &&
                !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await finder.ScanLocationAsync(root, progress, cts.Token));

        var session = Assert.IsType<CapturingSession>(repo.LastSession);

        Assert.Equal(0, session.CompleteCallCount);
        Assert.Single(session.FailCalls);
        Assert.True(session.FailCalls[0].Cancelled);
        Assert.False(string.IsNullOrWhiteSpace(session.FailCalls[0].Error));
        Assert.Equal(1, session.DisposeCallCount);

        Assert.Contains(reports, r => r.Phase == ScanPhase.Hashing);
    }

    // ----------------- Extra hash-focused tests -----------------

    [Fact]
    public async Task ScanLocation_HashDeterministicAcrossRuns()
    {
        var root = _fs.Dir("stablehash");
        var f1 = _fs.File("stablehash/a.txt", "SOME DATA"u8);
        var f2 = _fs.File("stablehash/b.txt", "OTHER DATA"u8);

        var repo1 = new CapturingRepo();
        var finder1 = new DuplicateFileFinder(repo1);
        await finder1.ScanLocationAsync(root, new Progress<DuplicateFileFinderProgressReport>());

        var session1 = Assert.IsType<CapturingSession>(repo1.LastSession);
        var hashesRun1 = session1.ObservedFiles.ToDictionary(
            f => PathUtils.NormalizePath(f.FullPath),
            f => f.Hash);

        var repo2 = new CapturingRepo();
        var finder2 = new DuplicateFileFinder(repo2);
        await finder2.ScanLocationAsync(root, new Progress<DuplicateFileFinderProgressReport>());

        var session2 = Assert.IsType<CapturingSession>(repo2.LastSession);
        var hashesRun2 = session2.ObservedFiles.ToDictionary(
            f => PathUtils.NormalizePath(f.FullPath),
            f => f.Hash);

        var path1 = PathUtils.NormalizePath(f1);
        var path2 = PathUtils.NormalizePath(f2);

        Assert.Equal(hashesRun1[path1], hashesRun2[path1]);
        Assert.Equal(hashesRun1[path2], hashesRun2[path2]);
    }

    [Fact]
    public async Task ScanLocation_HashDependsOnContent_NotPath()
    {
        var root = _fs.Dir("contenthash");
        var f1 = _fs.File("contenthash/a.txt", "ABC123"u8);
        var f2 = _fs.File("contenthash/b.txt", "XYZ789"u8);

        var repo = new CapturingRepo();
        var finder = new DuplicateFileFinder(repo);

        await finder.ScanLocationAsync(root, new Progress<DuplicateFileFinderProgressReport>());

        var session = Assert.IsType<CapturingSession>(repo.LastSession);

        Assert.Single(session.ObservedFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(f1) && f.Status == ScanEntryStatus.Enumerated);
        var f1Obs = Assert.Single(session.ObservedFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(f1) && f.Status == ScanEntryStatus.Hashed);
        Assert.Single(session.ObservedFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(f2) && f.Status == ScanEntryStatus.Enumerated);
        var f2Obs = Assert.Single(session.ObservedFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(f2) && f.Status == ScanEntryStatus.Hashed);

        Assert.NotEqual(f1Obs.Hash, f2Obs.Hash);
}

    [Fact]
    public async Task ScanLocation_SameContentAcrossDifferentRoots_ProducesSameHash()
    {
        var root1 = _fs.Dir("rootA");
        var root2 = _fs.Dir("rootB");

        var file1 = _fs.File("rootA/shared.txt", "SAME-CONTENT"u8);
        var file2 = _fs.File("rootB/shared.txt", "SAME-CONTENT"u8);

        var repo = new CapturingRepo();
        var finder = new DuplicateFileFinder(repo);

        await finder.ScanLocationAsync(root1, new Progress<DuplicateFileFinderProgressReport>());
        await finder.ScanLocationAsync(root2, new Progress<DuplicateFileFinderProgressReport>());

        Assert.Equal(2, repo.Sessions.Count);
    
        var session1 = repo.Sessions[0];
        var session2 = repo.Sessions[1];
    
        var f1Obs = Assert.Single(session1.ObservedFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(file1));
        var f2Obs = Assert.Single(session2.ObservedFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(file2));
    
        Assert.Equal(f1Obs.Hash, f2Obs.Hash);
    }
}
    
    
// // DuplicateFileFinderLibTests/DuplicateFileFinderTests.cs
//
// using System;
// using System.Collections.Generic;
// using System.IO;
// using System.Linq;
// using System.Threading;
// using System.Threading.Tasks;
// using DuplicateFileFinderLib.Core;
// using DuplicateFileFinderLib.IO;
// using DuplicateFileFinderLib.Repository;
// using DuplicateFileFinderLib.Repository.Models;
// using DuplicateFileFinderLib.Util;
// using DuplicateFileFinderLibTests.TestUtils;
// using Xunit;
// // ReSharper disable InconsistentNaming
//
// // ReSharper disable StringLiteralTypo
// // ReSharper disable RedundantArgumentDefaultValue
//
// namespace DuplicateFileFinderLibTests.Core;
//
// // ReSharper disable once InconsistentNaming
// public sealed class DuplicateFileFinder_E2E_Tests : IDisposable
// {
//     private readonly TempFsFixture _fs = new();
//
//     public void Dispose()
//     {
//         _fs.Dispose();
//     }
//
//     class FakeRepo : IRepo
//     {
//         public RepoViewSnapshot GetSnapshot()
//         {
//             throw new NotImplementedException();
//         }
//
//         public IReadOnlyList<ScanRun> ScanRunsView { get; } = null!;
//
//         public IScanSession BeginScan(string rootPath, int maxFilesBeforeFlush = 10000, int maxDirsBeforeFlush = 1000)
//         {
//             return new FakeSession();
//         }
//
//         public void CommitDelta(RepoDelta delta)
//         {
//         }
//
//         public Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default)
//         {
//             return Task.CompletedTask;
//         }
//
//         public void SaveSnapshot()
//         {
//         }
//
//         public void CompactIfNeeded(RepoCompactionPolicy? policy = null)
//         {
//         }
//
//         public void CompactNow()
//         {
//         }
//
//         public string GetFullDirPath(Guid dirId)
//         {
//             return "";
//         }
//     }
//
//     class FakeSession : IScanSession
//     {
//         public ScanRun Run { get; } = null!;
//         public long ScanSequence { get; } = 0;
//         public string RootPath { get; } = null!;
//
//         public ValueTask DisposeAsync()
//         {
//             return ValueTask.CompletedTask;
//         }
//
//         public Guid ObserveDirectory(string fullPath, ScanEntryStatus status, string? errorMessage = null)
//         {
//             return Guid.Empty;
//         }
//
//         public void ObserveFile(string fullFilePath, long size, HashKey hash, DateTimeOffset modified, DateTimeOffset created,
//             ScanEntryStatus status, string? errorMessage = null)
//         {
//         }
//
//         public Task FlushProgressAsync(CancellationToken cancellationToken = default)
//         {
//             return Task.CompletedTask;
//         }
//
//         public Task CompleteAsync(CancellationToken cancellationToken = default)
//         {
//             return Task.CompletedTask;
//         }
//
//         public Task FailAsync(string? errorMessage, bool cancelled, CancellationToken cancellationToken = default)
//         {
//             return Task.CompletedTask;
//         }
//     }
//     
//     [Fact]
//     public async Task ScanLocation_FindsDuplicates_AndAssignsGroups()
//     {
//         // arrange
//         // tempRoot/
//         //   a/file1.txt        ("HELLO")
//         //   b/file1copy.txt    ("HELLO")    -> duplicate of file1.txt
//         //   c/unique.bin       ("WORLD!")   -> unique (CS not calculated due size uniqueness
//         _fs.Dir("a");
//         _fs.Dir("b");
//         _fs.Dir("c");
//         var helloBytes = "HELLO"u8.ToArray();
//         var worldBytes = "WORLD!"u8.ToArray();
//         var f1 = _fs.File("a/file1.txt", helloBytes );
//         var f1Copy = _fs.File("b/file1copy.txt", helloBytes );
//         var uniq = _fs.File("c/unique.bin", worldBytes );
//
//         var finder = new DuplicateFileFinder(new FakeRepo());
//
//         // act        
//         var progress = new Progress<DuplicateFileFinderProgressReport>();
//         await finder.ScanLocationAsync(_fs.Root, progressIndicator: progress,
//             token: CancellationToken.None);
//
//         // dump CSV for inspection
//         await using var sw = new StringWriter();
//         finder.ExportToCsv(sw);
//         var csv = sw.ToString();
//         
//         var rows = CsvTestUtil.Parse(csv);
//
//         // assert
//         // 1. We expect to see our three files in the CSV
//         Assert.Contains(rows, r => r.Path == f1 && r.Kind == KindEnum.File);
//         Assert.Contains(rows, r => r.Path == f1Copy && r.Kind == KindEnum.File);
//         Assert.Contains(rows, r => r.Path == uniq && r.Kind == KindEnum.File);
//
//         // 2. Find their groups
//         var g1 = rows.First(r => r.Path == f1 && r.Kind == KindEnum.File).Group;
//         var g1C = rows.First(r => r.Path == f1Copy && r.Kind == KindEnum.File).Group;
//         var gu = rows.First(r => r.Path == uniq && r.Kind == KindEnum.File).Group;
//
//         // The two identical-content files should share the same non-negative group
//         Assert.Equal(g1, g1C);
//         Assert.True(g1 >= 0, "Expected duplicate files to be assigned a non-negative group id");
//
//         // The unique file should either not share that group,
//         // OR be marked with a sentinel negative group
//         Assert.True(gu != g1, "Unique file should not be grouped with duplicates");
//
//         // 3. Checksum should not be empty for duplicates (they got hashed)
//         var cs1 = rows.First(r => r.Path == f1 && r.Kind == KindEnum.File).Checksum;
//         var cs1C = rows.First(r => r.Path == f1Copy && r.Kind == KindEnum.File).Checksum;
//         Assert.False(string.IsNullOrWhiteSpace(cs1));
//         Assert.Equal(cs1, cs1C);
//
//         // 4. The unique file won't have had a checksum computed        
//         Assert.True(string.IsNullOrWhiteSpace(rows.First(r => r.Path == uniq && r.Kind == KindEnum.File).Checksum));
//     }
//
//     [Fact]
//     public async Task ScanLocation_PreCanceledToken_ThrowsAndDoesNotAddFiles()
//     {
//         // Arrange: small tree
//         var root = _fs.Dir("deep");
//         _fs.File("deep/f.bin", "X"u8.ToArray());
//     
//         var finder = new DuplicateFileFinder(new FakeRepo());
//         using var cts = new CancellationTokenSource();
//         // ReSharper disable once MethodHasAsyncOverload
//         cts.Cancel(); // deterministic: canceled before call
//     
//         // Act + Assert: must throw
//         await Assert.ThrowsAsync<OperationCanceledException>(async () =>
//             await finder.ScanLocationAsync(root, new Progress<DuplicateFileFinderProgressReport>(), cts.Token));
//     
//         // No files added
//         Assert.Equal(0, finder.TotalFilesScanned);
//     }
//
//     [Fact]
//     public async Task Scan_AncestorAfterDescendant_ScansSiblingSubtrees_AndPromotes_Correctly()
//     {
//         // Layout:
//         //   A/
//         //     B/
//         //       f1.txt  ("SAME")
//         //       f2.txt  ("SAME")     -> duplicates
//         //     C/
//         //       c.txt   ("CFILE")    -> sibling subtree under ancestor
//         //     u.txt     ("UNIQUE")   -> file directly under ancestor
//
//         var A = _fs.Dir("A");
//         var B = _fs.Dir("A/B");
//         var C = _fs.Dir("A/C");
//         var f1 = _fs.File(Path.Combine("A", "B", "f1.txt"), "SAME"u8.ToArray());
//         var f2 = _fs.File(Path.Combine("A", "B", "f2.txt"), "SAME"u8.ToArray());
//         var c = _fs.File(Path.Combine("A", "C", "c.txt"), "CFILE"u8.ToArray());
//         var u = _fs.File(Path.Combine("A", "u.txt"), "UNIQUE"u8.ToArray());
//
//         var dff = new DuplicateFileFinder(new FakeRepo());
//         var progress = new Progress<DuplicateFileFinderProgressReport>();
//
//         // 1) Scan the descendant first (B) — only B's files should be known
//         await dff.ScanLocationAsync(B, progress, CancellationToken.None);
//
//         var rootsAfterDescendant = dff.SearchPaths;
//         Assert.Single(rootsAfterDescendant);
//         Assert.Equal(PathUtils.NormalizePath(B), rootsAfterDescendant[0]);
//
//         var totalBefore = dff.TotalFilesScanned;
//         var wastedBytesBefore = dff.DuplicateSpaceBytes;
//         var wastedFilesBefore = dff.DuplicateFilesWastedCount;
//
//         // Only the two files in B should be counted now
//         Assert.Equal(2, totalBefore);
//         Assert.Equal(1, wastedFilesBefore); // one duplicate beyond representative
//         Assert.Equal("SAME".Length, wastedBytesBefore); // 4
//
//         // 2) Now scan the ancestor (A)
//         //    Expect: promotion to A as sole root, B stays under A, and the sibling subtree C + file u.txt are included.
//         await dff.ScanLocationAsync(A, progress, CancellationToken.None);
//
//         var rootsAfterAncestor = dff.SearchPaths;
//         Assert.Single(rootsAfterAncestor);
//         Assert.Equal(PathUtils.NormalizePath(A), rootsAfterAncestor[0]);
//
//         // Export and inspect rows
//         await using var sw = new StringWriter();
//         dff.ExportToCsv(sw);
//         var rows = CsvTestUtil.Parse(sw.ToString());
//
//         // Folder rows should include A, B, and C
//         Assert.Contains(rows, r => r.Kind == KindEnum.Folder && r.Path == A);
//         Assert.Contains(rows, r => r.Kind == KindEnum.Folder && r.Path == B);
//         Assert.Contains(rows, r => r.Kind == KindEnum.Folder && r.Path == C);
//
//         // All files must be present exactly once
//         Assert.Contains(rows, r => r.Kind == KindEnum.File && r.Path == f1);
//         Assert.Contains(rows, r => r.Kind == KindEnum.File && r.Path == f2);
//         Assert.Contains(rows, r => r.Kind == KindEnum.File && r.Path == c);
//         Assert.Contains(rows, r => r.Kind == KindEnum.File && r.Path == u);
//
//         Assert.Equal(1, rows.Count(r => r.Kind == KindEnum.File && r.Path == f1));
//         Assert.Equal(1, rows.Count(r => r.Kind == KindEnum.File && r.Path == f2));
//         Assert.Equal(1, rows.Count(r => r.Kind == KindEnum.File && r.Path == c));
//         Assert.Equal(1, rows.Count(r => r.Kind == KindEnum.File && r.Path == u));
//
//         // Totals should now include the sibling subtree file + ancestor file, with no double counting:
//         // previously 2 (B only) -> now 4 (B dup1+dup2 + C cFile + A u.txt)
//         Assert.Equal(4, dff.TotalFilesScanned);
//
//         // Duplicate metrics for the SAME pair should remain stable across promotion and sibling scan
//         Assert.Equal(wastedFilesBefore, dff.DuplicateFilesWastedCount);
//         Assert.Equal(wastedBytesBefore, dff.DuplicateSpaceBytes);
//
//         // The two SAME files remain grouped together; the others are not in that group
//         var g1 = rows.First(r => r.Kind == KindEnum.File && r.Path == f1).Group;
//         var g2 = rows.First(r => r.Kind == KindEnum.File && r.Path == f2).Group;
//         Assert.True(g1 >= 0);
//         Assert.Equal(g1, g2);
//
//         var gC = rows.First(r => r.Kind == KindEnum.File && r.Path == c).Group;
//         var gU = rows.First(r => r.Kind == KindEnum.File && r.Path == u).Group;
//         Assert.NotEqual(g1, gC);
//         Assert.NotEqual(g1, gU);
//     }
//
//
//     
//     [Fact]
//     public async Task Metrics_TotalFilesAndWastedBytes_AreCorrect()
//     {
//         var dir = _fs.Dir("metrics");
//         // Two identical files (size 4), one unique file (size 3)
//         _ = _fs.File("metrics/a.bin", "DATA"u8.ToArray());
//         _ = _fs.File("metrics/b.bin", "DATA"u8.ToArray());
//         _ = _fs.File("metrics/u.bin", "xyz"u8.ToArray());
//
//         var finder = new DuplicateFileFinder(new FakeRepo());
//         await finder.ScanLocationAsync(dir, new Progress<DuplicateFileFinderProgressReport>(), CancellationToken.None);
//
//         Assert.Equal(3, finder.TotalFilesScanned);
//
//         // One duplicate group of two 4-byte files => wasted bytes = 4 (keep one 4-byte representative).
//         Assert.Equal(4, finder.DuplicateSpaceBytes);
//         // Wasted file count (all but one representative) = 1
//         Assert.Equal(1, finder.DuplicateFilesWastedCount);
//     }
//     
//     [Fact]
//     public async Task GetDuplicateFileRows_ReturnsExpectedRows()
//     {
//         var root = _fs.Dir("rows");
//         var d1 = _fs.File("rows/d1.txt", "SAME"u8.ToArray());
//         var d2 = _fs.File("rows/d2.txt", "SAME"u8.ToArray());
//         _fs.File("rows/u1.txt", "DIFF"u8.ToArray());
//
//         var finder = new DuplicateFileFinder(new FakeRepo());
//         await finder.ScanLocationAsync(root, new Progress<DuplicateFileFinderProgressReport>(), CancellationToken.None);
//
//         var rows = await finder.GetDuplicateFileRowsAsync();
//
//         // Should include two rows (d1, d2) in the same group; and NOT include the unique file.
//         Assert.Equal(2, rows.Count);
//         Assert.All(rows, r => Assert.True(r.Group >= 0));
//         Assert.Contains(rows, r => r.Path == d1);
//         Assert.Contains(rows, r => r.Path == d2);
//     }
//     
//     [Fact]
//     public async Task ScanLocation_Cancels_DuringChecksumStage()
//     {
//         var root = _fs.Dir("hashcancel");
//
//         // Create many duplicate-sized files so producer queues a lot
//         for (int i = 0; i < 200; i++)
//             _fs.File(Path.Combine("hashcancel", $"f{i}.bin"), new byte[4096]); // all same size
//
//         var finder = new DuplicateFileFinder(new FakeRepo());
//         using var cts = new CancellationTokenSource();
//
//         // ReSharper disable AccessToDisposedClosure
//         var progress = new Progress<DuplicateFileFinderProgressReport>(_ =>
//         {
//             // Cancel shortly after scan starts reporting progress
//             if (!cts.IsCancellationRequested)
//                 cts.Cancel();
//         });
//
//         // We accept either an OperationCanceledException or a partial build
//         try
//         {
//             await finder.ScanLocationAsync(root, progress, cts.Token);
//         }
//         catch (OperationCanceledException)
//         {
//             /* ok */
//         }
//
//         // Still able to export CSV without crash
//         await using var sw = new StringWriter();
//         finder.ExportToCsv(sw);
//         Assert.NotNull(sw.ToString());
//     }
//
//     [Fact]
//     public async Task ExistingAncestorFirst_ScanningDescendantDoesNotAddNewRoot()
//     {
//         var a = _fs.Dir("X");
//         var b = _fs.Dir(Path.Combine("X", "Y"));
//         _fs.File(Path.Combine("X", "Y", "f.bin"), "Q"u8.ToArray());
//
//         var finder = new DuplicateFileFinder(new FakeRepo());
//         var progress = new Progress<DuplicateFileFinderProgressReport>();
//
//         await finder.ScanLocationAsync(a, progress, CancellationToken.None);
//         var roots1 = finder.SearchPaths;
//         Assert.Single(roots1);
//         Assert.Equal(PathUtils.NormalizePath(a), roots1[0]);
//
//         // Scan descendant — should not add another root
//         await finder.ScanLocationAsync(b, progress, CancellationToken.None);
//         var roots2 = finder.SearchPaths;
//         Assert.Single(roots2);
//         Assert.Equal(roots1[0], roots2[0]);
//     }
//     
//     
//
//     [Fact]
//     public async Task IndependentRootsRemainSeparate()
//     {
//         var leafDir = _fs.Dir(Path.Combine("B", "leaf"));
//         var cDir = _fs.Dir("C");
//         _fs.File(Path.Combine("B", "leaf", "b.txt"), "B"u8.ToArray());
//         _fs.File(Path.Combine("C", "c.txt"), "C"u8.ToArray());
//
//         var finder = new DuplicateFileFinder(new FakeRepo());
//         var progress = new Progress<DuplicateFileFinderProgressReport>();
//
//         await finder.ScanLocationAsync(leafDir, progress, CancellationToken.None);
//         await finder.ScanLocationAsync(cDir, progress, CancellationToken.None);
//
//         var roots = finder.SearchPaths;
//         Assert.Equal(2, roots.Count);
//         Assert.Contains(PathUtils.NormalizePath(leafDir),
//             roots);
//         Assert.Contains(PathUtils.NormalizePath(cDir), roots);
//     }
//     
//     [Fact]
//     public async Task Promotion_DoesNotDoubleCountOrRehash()
//     {
//         var a = _fs.Dir("P");
//         var b = _fs.Dir(Path.Combine("P", "Q"));
//         _fs.File(Path.Combine("P", "Q", "d1.txt"), "SAME"u8.ToArray());
//         _fs.File(Path.Combine("P", "Q", "d2.txt"), "SAME"u8.ToArray());
//
//         var finder = new DuplicateFileFinder(new FakeRepo());
//         var progress = new Progress<DuplicateFileFinderProgressReport>();
//
//         await finder.ScanLocationAsync(b, progress, CancellationToken.None);
//         var totalBefore = finder.TotalFilesScanned;
//         var wastedBefore = finder.DuplicateSpaceBytes;
//
//         // Promote
//         await finder.ScanLocationAsync(a, progress, CancellationToken.None);
//
//         // Should still be only two files counted, one duplicate wasted of size(len "SAME" = 4)
//         Assert.Equal(totalBefore, finder.TotalFilesScanned);
//         Assert.Equal(wastedBefore, finder.DuplicateSpaceBytes);
//     }
//     
//     [Fact]
//     public async Task ScanLocation_Failure_DoesNotMutateExistingState()
//     {
//         var dir = _fs.Dir("ok");
//         _fs.File("ok/a.txt", "A"u8.ToArray());
//
//         var finder = new DuplicateFileFinder(new FakeRepo());
//
//         // First, a successful scan (commits)
//         await finder.ScanLocationAsync(dir, new Progress<DuplicateFileFinderProgressReport>(), CancellationToken.None);
//         var csvBefore = new StringWriter(); finder.ExportToCsv(csvBefore);
//         var snapshot = csvBefore.ToString();
//
//         // Now scan a path that will throw early (missing root)
//         var missing = Path.Combine(_fs.Root, "does_not_exist");
//         await Assert.ThrowsAsync<OperationCanceledException>(async () =>
//         {
//             using var cts = new CancellationTokenSource();
//             cts.Cancel(); // force failure
//             await finder.ScanLocationAsync(missing, new Progress<DuplicateFileFinderProgressReport>(), cts.Token);
//         });
//
//         // State must be unchanged
//         var csvAfter = new StringWriter(); finder.ExportToCsv(csvAfter);
//         Assert.Equal(snapshot, csvAfter.ToString());
//     }
//     
//     [Fact]
//     public async Task ScanLocation_ExportIncludesCreationTime()
//     {
//         using var fs = new TempFsFixture();
//         var created = new DateTimeOffset(2023, 10, 21, 12, 34, 56, TimeSpan.Zero);
//
//         var root = fs.Dir("root");
//         var f1 = fs.File("root/a.txt", "HELLO"u8, created);
//         var f2 = fs.File("root/b.txt", "HELLO"u8, created.AddMinutes(1));
//
//         var dff = new DuplicateFileFinder(new FakeRepo());
//         await dff.ScanLocationAsync(root, progressIndicator: null, token: default);
//
//         using var sw = new StringWriter();
//         dff.ExportToCsv(sw);
//         var rows = CsvTestUtil.Parse(sw.ToString());
//
//         Assert.Equal(CsvSpec.Header.Length, CsvSpec.Header.Length); // header known
//         AssertRows.ContainsFolder(rows, root);
//         AssertRows.ContainsFile(rows, f1);
//         AssertRows.ContainsFile(rows, f2);
//
//         // Creation times are recorded as UTC in CSV
//         AssertRows.CreationTimeIs(rows, f1, created);
//         AssertRows.CreationTimeIs(rows, f2, created.AddMinutes(1));
//
//         // The two identical files still group together
//         AssertRows.InSameGroup(rows, f1, f2);
//     }
//
// [Fact]
// public async Task Csv_RoundTrip_IncludesCreationTime_And_GroupsByContent()
// {
//     using var fs = new TempFsFixture();
//     var created1 = new DateTimeOffset(2023, 10, 21, 12, 34, 56, TimeSpan.Zero);
//     var created2 = created1.AddMinutes(1);
//
//     var root = fs.Dir("root");
//     var f1 = fs.File("root/a.txt", "HELLO"u8, created1);
//     var f2 = fs.File("root/b.txt", "HELLO"u8, created2);
//
//     var dff = new DuplicateFileFinder(new FakeRepo());
//     await dff.ScanLocationAsync(root);
//
//     await using var sw = new StringWriter();
//     dff.ExportToCsv(sw);
//     var csv = sw.ToString();
//
//     // parse with single source of truth
//     var rows = CsvTestUtil.Parse(csv);
//
//     AssertRows.ContainsFolder(rows, root);
//     AssertRows.ContainsFile(rows, f1);
//     AssertRows.ContainsFile(rows, f2);
//
//     AssertRows.CreationTimeIs(rows, f1, created1);
//     AssertRows.CreationTimeIs(rows, f2, created2);
//
//     AssertRows.InSameGroup(rows, f1, f2);
// }
// }