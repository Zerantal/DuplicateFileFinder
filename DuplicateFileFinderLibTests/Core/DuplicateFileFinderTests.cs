// DuplicateFileFinderLibTests/Core/DuplicateFileFinder_Repo_Tests.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Util;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;
// ReSharper disable UnusedAutoPropertyAccessor.Local
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
// ReSharper disable AccessToDisposedClosure
// ReSharper disable UnusedMember.Local

namespace DuplicateFileFinderLibTests.Core;

// NOTE: These tests deliberately do NOT exercise the legacy _root / CSV / metrics APIs.
// They focus only on Repo-facing behavior of DuplicateFileFinder.
public sealed class DuplicateFileFinderRepoTests : IDisposable
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

    private sealed class CapturingSession(string rootPath) : IScanSession
    {
        public ScanRun Run { get; } = new()
        {
            ScanSequence = 1,
            RootPath = rootPath,
            StartedAt = DateTimeOffset.UtcNow,
            Status = ScanRunStatus.InProgress
        };

        public long ScanSequence => Run.ScanSequence;
        public string RootPath => Run.RootPath;

        public readonly List<ObservedDir> ObservedDirectories = new();
        public readonly List<ObservedFile> ObservedFiles = new();

        public List<ObservedDir> FinalDirs => ObservedDirectories.GroupBy(d => d.FullPath).Select(g => g.Last()).ToList();
        public List<ObservedFile> FinalFiles => ObservedFiles.GroupBy(f => f.FullPath).Select(f => f.Last()).ToList();
        
        public int FlushCallCount { get; private set; }
        public int CompleteCallCount { get; private set; }
        public List<(string? Error, bool Cancelled)> FailCalls { get; } = new();
        public int DisposeCallCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }

        public Guid AddOrUpdateDirectory(string fullPath, ScanEntryStatus? status = null, string? errorMessage = null)
        {
            var lastRecordDirEntry = ObservedDirectories.LastOrDefault(f => f.FullPath == fullPath);
            status ??= lastRecordDirEntry?.Status ?? ScanEntryStatus.Enumerated;
            ObservedDirectories.Add(new ObservedDir(fullPath, status.Value, errorMessage));
            return Guid.NewGuid();
        }

        public void AddOrUpdateFile(string fullFilePath, long? size = null, HashKey? hash = null, DateTimeOffset? modified = null,
            DateTimeOffset? created = null, ScanEntryStatus? status = null, string? errorMessage = null)
        {
            var existingFile = ObservedFiles.LastOrDefault(f => f.FullPath == fullFilePath);
            size ??= existingFile?.Size ?? 0;
            created ??= existingFile?.Created ?? default(DateTimeOffset);
            
            ObservedFiles.Add(
                new ObservedFile(
                    fullFilePath,
                    size.Value,
                    hash ?? HashKey.NotComputed,
                    modified ?? default(DateTimeOffset),
                    created.Value,
                    status ?? ScanEntryStatus.Enumerated,
                    errorMessage));
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

    private sealed class ObservedDir(string fullPath, ScanEntryStatus status, string? error)
    {
        public string FullPath { get; } = fullPath;
        public ScanEntryStatus Status { get; } = status;
        public string? Error { get; } = error;
    }
    private sealed class ObservedFile(
        string fullPath,
        long size,
        HashKey hash,
        DateTimeOffset modified,
        DateTimeOffset created,
        ScanEntryStatus status,
        string? errorMessage)
    {
        public string FullPath { get; } = fullPath;
        public long Size { get; } = size;
        public HashKey Hash { get; } = hash;
        public DateTimeOffset Modified { get; } = modified;
        public DateTimeOffset Created { get; } = created;
        public ScanEntryStatus Status { get; } = status;
        public string? ErrorMessage { get; } = errorMessage;
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
        var hashesRun1 = session1.FinalFiles.ToDictionary(
            f => PathUtils.NormalizePath(f.FullPath),
            f => f.Hash);

        var repo2 = new CapturingRepo();
        var finder2 = new DuplicateFileFinder(repo2);
        await finder2.ScanLocationAsync(root, new Progress<DuplicateFileFinderProgressReport>());

        var session2 = Assert.IsType<CapturingSession>(repo2.LastSession);
        var hashesRun2 = session2.FinalFiles.ToDictionary(
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
    
        var f1Obs = Assert.Single(session1.FinalFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(file1));
        var f2Obs = Assert.Single(session2.FinalFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(file2));
    
        Assert.Equal(f1Obs.Hash, f2Obs.Hash);
    }
}