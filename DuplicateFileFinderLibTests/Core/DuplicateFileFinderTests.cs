// DuplicateFileFinderLibTests/Core/DuplicateFileFinder_Repo_Tests.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Util;
using DuplicateFileFinderLibTests.TestUtils;
using DuplicateFileFinderLibTests.TestUtils.Fakes;
using Xunit;
using Repo = DuplicateFileFinderLib.Repository.Core.Repo;

// ReSharper disable UnusedAutoPropertyAccessor.Local
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
// ReSharper disable AccessToDisposedClosure
// ReSharper disable UnusedMember.Local

namespace DuplicateFileFinderLibTests.Core;

// NOTE: These tests deliberately do NOT exercise the legacy _root / CSV / metrics APIs.
// They focus only on Repo-facing behavior of DuplicateFileFinder.
public sealed class DuplicateFileFinderRepoTests : IDisposable
{
    private readonly TempFsFixture _fs = new("DFF");

    public void Dispose()
    {
        _fs.Dispose();
    }

    // ----------------- Support Types -----------------

    private sealed class SynchronousProgress<T>(Action<T> action) : IProgress<T>
    {
        private readonly List<T> _progressLog = [];

        public IReadOnlyList<T> ProgressLog => _progressLog;

        public void Report(T value)
        {
            _progressLog.Add(value);
            action(value);
        }
    }

    private sealed class CapturingHost : IRepoHost
    {
        private readonly TempFsFixture _repoDir = new TempFsFixture("DFF_repo");
        public CapturingHost(IRepo repo)
        {
            Repo = repo;
            HashIndex = new HashIndexPlugin(_repoDir.Root);
            TreeIndex = new TreeIndexPlugin(_repoDir.Root);
        }

        public ValueTask DisposeAsync()
        {
            throw new NotImplementedException();
        }

        public IRepo Repo { get; }
        public IHashIndexReadModel HashIndex { get; }
        public ITreeIndexReadModel TreeIndex { get; }
    }

    private sealed class FakeRepoView : IRepoView
    {
        public IReadOnlyDictionary<long, DirRecord> Dirs { get; } = new Dictionary<long, DirRecord>();
        public IReadOnlyDictionary<long, FileRecord> Files { get; } = new Dictionary<long, FileRecord>();
        public DirRecord? TryGetDir(long dirId)
        {
            return null;
        }

        public FileRecord? TryGetFile(long fileId)
        {
            return null;
        }
    }
    
    private sealed class CapturingRepo : IRepo
    {
        public readonly List<string> BeginScanRoots = new();
        public readonly List<CapturingScanSession> Sessions = new();
        public CapturingScanSession? LastSession { get; private set; }
        public bool CompactIfNeededCalled { get; private set; }
        
        public bool CompactAsyncCalled { get; private set; }

        public IRepoView GetRepoView() => new FakeRepoView();

        public IReadOnlyList<ScanRun> ScanRunsView { get; } = [];
        public IReadOnlyList<ScanRoot> ScanRootsView { get; } = [];

        public IScanSession BeginScan(string rootPath, ScanOperation scanOperation = ScanOperation.FullScan, VolumeInfo? volumeInfo = null,
            int maxFilesBeforeFlush = 10000, int maxDirsBeforeFlush = 1000)
        {
            var session = new CapturingScanSession(rootPath);
            LastSession = session;
            Sessions.Add(session);
            BeginScanRoots.Add(rootPath);
            
            return session;
        }

        public void CommitDelta(RepoDelta delta) { }

        public Task CommitDeltaAsync(RepoDelta delta, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void SaveScanSnapshots()
        {
        }

        public Task CompactAsync(RepoCompactionPolicy? policy = null, CancellationToken ct = default)
        {
            CompactAsyncCalled = true;
            return Task.CompletedTask;
        }

        public string GetDirPath(long dirId, bool relativeToVolumePath = false)
        {
            throw new NotImplementedException();
        }

        public void SaveSnapshot() { }

        public void CompactIfNeeded()
            => CompactIfNeededCalled = true;

        public void CompactNow() { }

        public long AllocateRunId()
        {
            throw new NotImplementedException();
        }

        public IReadOnlyList<IReadOnlyList<FileRecord>> GetDuplicateGroups()
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public ValueTask DisposeAsync()
        {
            throw new NotImplementedException();
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
        var host = new CapturingHost(repo);
        var finder = new DuplicateFileFinder(host);

        // Act
        var progress = new Progress<DuplicateFileFinderProgressReport>();
        await finder.FullScanAsync(_fs.Root, progress: progress, ct: TestContext.Current.CancellationToken);

        var session = Assert.IsType<CapturingScanSession>(repo.LastSession);

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
        Assert.True(repo.CompactAsyncCalled);
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
        var host = new CapturingHost(repo);
        var finder = new DuplicateFileFinder(host);

        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);

        var session = Assert.IsType<CapturingScanSession>(repo.LastSession);

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
        var host = new  CapturingHost(repo);
        var finder = new DuplicateFileFinder(host, false);
        
        var progress = new SynchronousProgress<DuplicateFileFinderProgressReport>(_ => { });
        
        await finder.FullScanAsync(root, progress: progress, ct: TestContext.Current.CancellationToken);

        var report = progress.ProgressLog.ToList();
        
        Assert.NotEmpty(report);

        var last = report[^1];
        Assert.Equal(ScanPhase.Completed, last.Phase);
        Assert.False(last.IsRunning);
        Assert.Equal(1.0, last.PercentComplete, 5e-3);

        Assert.Contains(report, r => r.Phase == ScanPhase.Enumerating);
        var lastEnumIdx = report.FindLastIndex(r => r.Phase == ScanPhase.Enumerating);
        Assert.Contains(report[(lastEnumIdx + 1)..], r => r.Phase == ScanPhase.Hashing);
    }

    [Fact]
    public async Task ScanLocation_MultipleCalls_BeginScanCalledPerScan()
    {
        var root1 = _fs.Dir("root1");
        var root2 = _fs.Dir("root2");
        _fs.File("root1/a.bin", "A"u8);
        _fs.File("root2/b.bin", "B"u8);

        var repo = new CapturingRepo();
        var host = new CapturingHost(repo);
        var finder = new DuplicateFileFinder(host);
        var progress = new Progress<DuplicateFileFinderProgressReport>();

        await finder.FullScanAsync(root1, progress: progress, ct: TestContext.Current.CancellationToken);
        await finder.FullScanAsync(root2, progress: progress, ct: TestContext.Current.CancellationToken);

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
        var host = new  CapturingHost(repo);
        var finder = new DuplicateFileFinder(host);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // canceled before the scan starts

        // Act + Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await finder.FullScanAsync(root, ct: cts.Token));

        var session = Assert.IsType<CapturingScanSession>(repo.LastSession);

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
        var host = new  CapturingHost(repo);
        var finder = new DuplicateFileFinder(host);
        
        // Act: expect some exception (e.g. DirectoryNotFoundException)
        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
            await finder.FullScanAsync(missingRoot, ct: TestContext.Current.CancellationToken));


        var session = Assert.IsType<CapturingScanSession>(repo.LastSession);

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
        var host = new  CapturingHost(repo);
        var finder = new DuplicateFileFinder(host);
        
        using var cts = new CancellationTokenSource();
        
        var progress = new SynchronousProgress<DuplicateFileFinderProgressReport>(r =>
        {
            if (r is { Phase: ScanPhase.Enumerating, Processed: >= 1 })
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await finder.FullScanAsync(root, progress: progress, ct: cts.Token));

        var session = Assert.IsType<CapturingScanSession>(repo.LastSession);

        Assert.Equal(0, session.CompleteCallCount);
        Assert.Single(session.FailCalls);
        Assert.True(session.FailCalls[0].Cancelled);
        Assert.False(string.IsNullOrWhiteSpace(session.FailCalls[0].Error));
        Assert.Equal(1, session.DisposeCallCount);

        Assert.Contains(progress.ProgressLog, r => r.Phase == ScanPhase.Enumerating);
    }

    [Fact]
    public async Task ScanLocation_CancellationDuringHashing_SetsFailCancelledTrue()
    {
        var root = _fs.Dir("hashcancel");
        for (var i = 0; i < 50; i++)
            _fs.File(Path.Combine("hashcancel", $"f{i}.bin"), new byte[4096]);

        var repo = new CapturingRepo();
        var host = new  CapturingHost(repo);
        var finder = new DuplicateFileFinder(host);
        
        using var cts = new CancellationTokenSource();
        var reports = new List<DuplicateFileFinderProgressReport>();

        var progress = new Progress<DuplicateFileFinderProgressReport>(r =>
        {
            reports.Add(r);
            if (r is { Phase: ScanPhase.Hashing, Processed: > 0 } &&
                !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await finder.FullScanAsync(root, progress: progress, ct: cts.Token));

        var session = Assert.IsType<CapturingScanSession>(repo.LastSession);

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
        var host1 = new  CapturingHost(repo1);
        var finder1 = new DuplicateFileFinder(host1);
        await finder1.FullScanAsync(root, ct: TestContext.Current.CancellationToken);

        var session1 = Assert.IsType<CapturingScanSession>(repo1.LastSession);
        var hashesRun1 = session1.FinalFiles.ToDictionary(
            f => PathUtils.NormalizePath(f.FullPath),
            f => f.Hash);

        var repo2 = new CapturingRepo();
        var host2 = new  CapturingHost(repo2);
        var finder2 = new DuplicateFileFinder(host2);
        await finder2.FullScanAsync(root, ct: TestContext.Current.CancellationToken);

        var session2 = Assert.IsType<CapturingScanSession>(repo2.LastSession);
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
        var host = new  CapturingHost(repo);
        var finder = new DuplicateFileFinder(host);
        
        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);

        var session = Assert.IsType<CapturingScanSession>(repo.LastSession);

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
        var host = new  CapturingHost(repo);
        var finder = new DuplicateFileFinder(host);
        
        await finder.FullScanAsync(root1, ct: TestContext.Current.CancellationToken);
        await finder.FullScanAsync(root2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, repo.Sessions.Count);
    
        var session1 = repo.Sessions[0];
        var session2 = repo.Sessions[1];
    
        var f1Obs = Assert.Single(session1.FinalFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(file1));
        var f2Obs = Assert.Single(session2.FinalFiles,
            f => PathUtils.NormalizePath(f.FullPath) == PathUtils.NormalizePath(file2));
    
        Assert.Equal(f1Obs.Hash, f2Obs.Hash);
    }
    
    // private static async Task<Repo> CreateRepo(string root)
    // {
    //     var repoDir = Path.Combine(root, "repo");
    //     Directory.CreateDirectory(repoDir);
    //     return await Repo.OpenAsync(repoDir);
    // }

    private static async Task<IRepoHost> CreateHost(string root)
    {
        var repoDir = Path.Combine(root, "repo");
        Directory.CreateDirectory(repoDir);
        return await RepoHost.OpenAsync(repoDir);
    }

    private static Dictionary<string, FileRecord> MapFilesByFullPath(IRepo repo, IRepoView snapshot)
    {
        var result = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in snapshot.Files)
        {
            var file = kv.Value;
            var dirPath = PathUtils.NormalizePath(repo.GetDirPath(file.DirId));
            var fullPath = PathUtils.NormalizePath(Path.Combine(dirPath, file.Name));
            result[fullPath] = file;
        }

        return result;
    }

    private static HashSet<string> MapDirsByFullPath(IRepo repo, IRepoView snapshot)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in snapshot.Dirs)
        {
            var dirPath = PathUtils.NormalizePath(repo.GetDirPath(kv.Key));
            result.Add(dirPath);
        }

        return result;
    }
    
    [Fact]
    public async Task FullScanThenRescan_WithDeletedFile_RemovesFileFromRepoAndHashIndex()
    {
        // Arrange: root/
        //   keep.bin   (kept across rescan)
        //   delete.bin (deleted before second scan)
        var root = _fs.Dir("root");
        var keepPath   = _fs.File("root/keep.bin",   "AAAA"u8.ToArray());
        var deletePath = _fs.File("root/delete.bin", "BBBB"u8.ToArray());

        // var repo    = await CreateRepo(_fs.Root);
        var host = await CreateHost(_fs.Root);
        var repo = host.Repo;
        var finder = new DuplicateFileFinder(host);
        
        // First scan
        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);

        var snap1        = repo.GetRepoView();
        var filesByPath1 = MapFilesByFullPath(repo, snap1);

        // Sanity: both files are present after first scan
        Assert.Contains(
            filesByPath1.Keys,
            p => PathUtils.IsSamePath(p, keepPath));
        Assert.Contains(
            filesByPath1.Keys,
            p => PathUtils.IsSamePath(p, deletePath));

        // Act: delete the file on disk and rescan the same root
        File.Delete(deletePath);

        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);

        var snap2        = repo.GetRepoView();
        var filesByPath2 = MapFilesByFullPath(repo, snap2);

        // Assert: kept file is still present, deleted file is gone
        Assert.Contains(
            filesByPath2.Keys,
            p => PathUtils.IsSamePath(p, keepPath));
        Assert.DoesNotContain(
            filesByPath2.Keys,
            p => PathUtils.IsSamePath(p, deletePath));
    }
    
    [Fact]
    public async Task FullScanThenRescan_WithDeletedDirectory_RemovesDirectoryAndChildrenFromRepo()
    {
        // Arrange: root/
        //   keep.bin              (kept across rescan)
        //   sub/child.bin         (directory and file both deleted)
        var root = _fs.Dir("root");
        var keepPath = _fs.File("root/keep.bin", "AAAA"u8.ToArray());
        _fs.Dir("root/sub");
        var childPath = _fs.File("root/sub/child.bin", "CCCC"u8.ToArray());

        // var repo    = await CreateRepo(_fs.Root);
        var host = await CreateHost(_fs.Root);
        var repo = host.Repo;
        var finder = new DuplicateFileFinder(host);
        
        // First scan
        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);

        var snap1        = repo.GetRepoView();
        var filesByPath1 = MapFilesByFullPath(repo, snap1);
        var dirs1        = MapDirsByFullPath(repo, snap1);

        var normRoot = PathUtils.NormalizePath(root);
        var normSub  = PathUtils.NormalizePath(Path.Combine(root, "sub"));

        // Sanity: root dir, sub dir, and both files exist in repo
        Assert.Contains(dirs1, p => PathUtils.IsSamePath(p, normRoot));
        Assert.Contains(dirs1, p => PathUtils.IsSamePath(p, normSub));

        Assert.Contains(filesByPath1.Keys, p => PathUtils.IsSamePath(p, keepPath));
        Assert.Contains(filesByPath1.Keys, p => PathUtils.IsSamePath(p, childPath));

        // Act: delete the sub directory (and its child) and rescan the same root
        Directory.Delete(normSub, recursive: true);

        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);

        var snap2        = repo.GetRepoView();
        var filesByPath2 = MapFilesByFullPath(repo, snap2);
        var dirs2        = MapDirsByFullPath(repo, snap2);

        // Assert: root directory still present, sub directory removed
        Assert.Contains(dirs2, p => PathUtils.IsSamePath(p, normRoot));
        Assert.DoesNotContain(dirs2, p => PathUtils.IsSamePath(p, normSub));

        // Kept file still present, child file removed
        Assert.Contains(filesByPath2.Keys, p => PathUtils.IsSamePath(p, keepPath));
        Assert.DoesNotContain(filesByPath2.Keys, p => PathUtils.IsSamePath(p, childPath));
    }
    
    [Fact]
    public async Task FullRescan_WithoutChanges_DoesNotDeleteOrChangeFilesOrDirs()
    {
        // Arrange
        var root = _fs.Dir("root");
        _fs.File("root/a.bin", "AAAA"u8.ToArray());
        _fs.File("root/b.bin", "BBBB"u8.ToArray());
        _fs.Dir("root/sub");
        _fs.File("root/sub/c.bin", "CCCC"u8.ToArray());
        
        var host = await CreateHost(_fs.Root);
        var repo = host.Repo;
        var finder = new DuplicateFileFinder(host);
        
        // First scan
        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);

        var snap1  = repo.GetRepoView();
        var files1 = MapFilesByFullPath(repo, snap1);
        var dirs1  = MapDirsByFullPath(repo, snap1);

        // Second scan (full rescan, no changes on disk)
        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);

        var snap2  = repo.GetRepoView();
        var files2 = MapFilesByFullPath(repo, snap2);
        var dirs2  = MapDirsByFullPath(repo, snap2);

        // Assert: same dirs, no deletions
        Assert.Equal(dirs1.Count, dirs2.Count);
        Assert.True(dirs1.SetEquals(dirs2));

        // Assert: same files by path
        Assert.Equal(files1.Count, files2.Count);
        Assert.True(files1.Keys.SequenceEqual(files2.Keys));

        foreach (var path in files1.Keys)
        {
            var a = files1[path];
            var b = files2[path];

            Assert.Equal(a.Size, b.Size);
            Assert.Equal(a.Hash, b.Hash);
            Assert.False(a.Status.HasFlag(ScanEntryStatus.Deleted));
            Assert.False(b.Status.HasFlag(ScanEntryStatus.Deleted));
            Assert.Equal(a.ErrorMessage, b.ErrorMessage);
        }
    }

    [Fact]
    public async Task QuickRescan_WithoutChanges_DoesNotDeleteOrChangeFilesOrDirs()
    {
        // Arrange
        var root = _fs.Dir("root");
        _fs.File("root/a.bin", "AAAA"u8.ToArray());
        _fs.File("root/b.bin", "BBBB"u8.ToArray());
        _fs.Dir("root/sub");
        _fs.File("root/sub/c.bin", "CCCC"u8.ToArray());
        
        var host = await CreateHost(_fs.Root);
        var repo = host.Repo;
        var finder = new DuplicateFileFinder(host);
        
        // First scan (full)
        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);
    
        var snap1  = repo.GetRepoView();
        var files1 = MapFilesByFullPath(repo, snap1);
        var dirs1  = MapDirsByFullPath(repo, snap1);
    
        // QuickScan rescan (no changes on disk)
        await finder.QuickScanAsync(root, ct: TestContext.Current.CancellationToken);
    
        var snap2  = repo.GetRepoView();
        var files2 = MapFilesByFullPath(repo, snap2);
        var dirs2  = MapDirsByFullPath(repo, snap2);
    
        // Assert: same dirs, no deletions
        Assert.Equal(dirs1.Count, dirs2.Count);
        Assert.True(dirs1.SetEquals(dirs2));
    
        // Assert: same files by path
        Assert.Equal(files1.Count, files2.Count);
        Assert.True(files1.Keys.SequenceEqual(files2.Keys));
    
        foreach (var path in files1.Keys)
        {
            var a = files1[path];
            var b = files2[path];
    
            Assert.Equal(a.Size, b.Size);
            Assert.Equal(a.Hash, b.Hash);
            Assert.False(a.Status.HasFlag(ScanEntryStatus.Deleted));
            Assert.False(b.Status.HasFlag(ScanEntryStatus.Deleted));
            Assert.Equal(a.ErrorMessage, b.ErrorMessage);
        }
    }

    [Fact]
    public async Task QuickRescan_WithDeletedFile_RemovesFileAndHashIndexEntry()
    {
        // Arrange
        var root       = _fs.Dir("root");
        var keepPath   = _fs.File("root/keep.bin",   "AAAA"u8.ToArray());
        var deletePath = _fs.File("root/delete.bin", "BBBB"u8.ToArray());
        
        var host = await CreateHost(_fs.Root);
        var repo = host.Repo;
        var finder = new DuplicateFileFinder(host);
        
        // First scan
        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);
    
        var snap1  = repo.GetRepoView();
        var files1 = MapFilesByFullPath(repo, snap1);
    
        // Sanity: both files present
        Assert.Contains(files1.Keys, p => PathUtils.IsSamePath(p, keepPath));
        Assert.Contains(files1.Keys, p => PathUtils.IsSamePath(p, deletePath));
    
        // Delete file on disk
        File.Delete(deletePath);
    
        // Act: quick rescan
        await finder.QuickScanAsync(root, ct: TestContext.Current.CancellationToken);
    
        var snap2  = repo.GetRepoView();
        var files2 = MapFilesByFullPath(repo, snap2);
    
        // Assert: kept file remains, deleted file gone
        Assert.Contains(files2.Keys, p => PathUtils.IsSamePath(p, keepPath));
        Assert.DoesNotContain(files2.Keys, p => PathUtils.IsSamePath(p, deletePath));
    }
    
    [Fact]
    public async Task QuickRescan_WithDeletedDirectory_RemovesDirectoryAndChildren()
    {
        // Arrange
        var root     = _fs.Dir("root");
        var keepPath = _fs.File("root/keep.bin", "AAAA"u8.ToArray());
        _fs.Dir("root/sub");
        var childPath = _fs.File("root/sub/child.bin", "CCCC"u8.ToArray());
        
        var host = await CreateHost(_fs.Root);
        var repo = host.Repo;
        var finder = new DuplicateFileFinder(host);
        
        // First scan
        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);
    
        var snap1  = repo.GetRepoView();
        var files1 = MapFilesByFullPath(repo, snap1);
        var dirs1  = MapDirsByFullPath(repo, snap1);
    
        var normRoot = PathUtils.NormalizePath(root);
        var normSub  = PathUtils.NormalizePath(Path.Combine(root, "sub"));
    
        // Sanity: root dir, sub dir, and both files exist
        Assert.Contains(dirs1, p => PathUtils.IsSamePath(p, normRoot));
        Assert.Contains(dirs1, p => PathUtils.IsSamePath(p, normSub));
        Assert.Contains(files1.Keys, p => PathUtils.IsSamePath(p, keepPath));
        Assert.Contains(files1.Keys, p => PathUtils.IsSamePath(p, childPath));
    
        // Delete sub directory on disk
        Directory.Delete(normSub, recursive: true);
    
        // Act: quick rescan
        await finder.QuickScanAsync(root, ct: TestContext.Current.CancellationToken);
    
        var snap2  = repo.GetRepoView();
        var files2 = MapFilesByFullPath(repo, snap2);
        var dirs2  = MapDirsByFullPath(repo, snap2);
    
        // Assert: root dir remains, sub dir removed
        Assert.Contains(dirs2, p => PathUtils.IsSamePath(p, normRoot));
        Assert.DoesNotContain(dirs2, p => PathUtils.IsSamePath(p, normSub));
    
        // Kept file remains, child file removed
        Assert.Contains(files2.Keys, p => PathUtils.IsSamePath(p, keepPath));
        Assert.DoesNotContain(files2.Keys, p => PathUtils.IsSamePath(p, childPath));
    }

    [Fact]
    public async Task QuickRescan_WithModifiedFile_UpdatesHashOnlyForChangedFile()
    {
        // Arrange
        var root       = _fs.Dir("root");
        var keepPath   = _fs.File("root/keep.bin",   "AAAA"u8.ToArray());
        var changePath = _fs.File("root/change.bin", "BBBB"u8.ToArray());
    
        var host = await CreateHost(_fs.Root);
        var repo = host.Repo;
        var finder = new DuplicateFileFinder(host);
        
        // First scan
        await finder.FullScanAsync(root, ct: TestContext.Current.CancellationToken);
    
        var snap1  = repo.GetRepoView();
        var files1 = MapFilesByFullPath(repo, snap1);
    
        var keep1   = files1.Single(kv => PathUtils.IsSamePath(kv.Key, keepPath)).Value;
        var change1 = files1.Single(kv => PathUtils.IsSamePath(kv.Key, changePath)).Value;
    
        // Modify one file on disk
        await File.WriteAllBytesAsync(changePath, "CCCC"u8.ToArray(), TestContext.Current.CancellationToken);
    
        // Act: quick rescan
        await finder.QuickScanAsync(root, ct: TestContext.Current.CancellationToken);
    
        var snap2  = repo.GetRepoView();
        var files2 = MapFilesByFullPath(repo, snap2);
    
        var keep2   = files2.Single(kv => PathUtils.IsSamePath(kv.Key, keepPath)).Value;
        var change2 = files2.Single(kv => PathUtils.IsSamePath(kv.Key, changePath)).Value;
    
        // Kept file: unchanged
        Assert.Equal(keep1.Hash, keep2.Hash);
        Assert.Equal(keep1.Size, keep2.Size);
        Assert.Equal(keep1.Status, keep2.Status);
    
        // Changed file: hash and modified time updated, still hashed
        Assert.NotEqual(change1.Hash, change2.Hash);
        Assert.NotEqual(change1.Modified, change2.Modified);
        Assert.Equal(ScanEntryStatus.Hashed, change2.Status);
    }
    
    [Fact]
    public async Task FileNameWithBackslash_IsNotSplitIntoDirectory_OnUnix()
    {
        // This edge case only applies on Unix-like systems where '\' is allowed in file names.
        if (Path.DirectorySeparatorChar == '\\')
            return; // On Windows paths with '\' in the file name cannot exist; nothing to test.

        // Arrange
        var root = _fs.Dir("root");
        var netDir = _fs.Dir("root/net9.0");

        // Create a file whose name contains a backslash. On Unix this is legal and should
        // NOT be split into a "TestData" directory + file.
        const string fileNameWithBackslash = "TestData\\ScanLocationTest_actual.csv";
        var fullPath = Path.Combine(netDir, fileNameWithBackslash);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, "HELLO"u8.ToArray(), TestContext.Current.CancellationToken);

        // Repo + scanner
        var repoDir = Path.Combine(_fs.Root, ".repo");
        Directory.CreateDirectory(repoDir);
        var repo   = await Repo.OpenAsync(repoDir, TestContext.Current.CancellationToken);
        var host = new  CapturingHost(repo);
        var finder = new DuplicateFileFinder(host);
        
        // Act
        await finder.FullScanAsync(root, progress: null, ct: CancellationToken.None);

        var snapshot = repo.GetRepoView();

        // Find the net9.0 directory in the repo
        var expectedNetDirPath = PathUtils.NormalizePath(netDir);
        var netDirRecord = snapshot.Dirs.Values.Single(d =>
            PathUtils.IsSamePath(repo.GetDirPath(d.DirId), expectedNetDirPath));

        // Assert 1: no child directory called "TestData" under net9.0
        var childDirNames = snapshot.Dirs.Values
            .Where(d => d.ParentDirId == netDirRecord.DirId)
            .Select(d => d.Name)
            .ToList();

        Assert.DoesNotContain("TestData", childDirNames);

        // Assert 2: there is a file whose *name* includes the backslash and whose
        // full path matches the actual file we created, and it lives directly under net9.0.
        var matchingFile = snapshot.Files.Values.SingleOrDefault(f =>
        {
            var dirPath = repo.GetDirPath(f.DirId);
            var repoFullPath = Path.Combine(dirPath, f.Name);
            return PathUtils.IsSamePath(repoFullPath, fullPath);
        });

        Assert.NotNull(matchingFile);
        Assert.Equal(netDirRecord.DirId, matchingFile.DirId);
        Assert.Equal(fileNameWithBackslash, matchingFile.Name);
    }
}