// DuplicateFileFinderLibTests/Core/FullScanOperationMoqTests.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.Scan;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;
using DuplicateFileFinderLib.Util;

using DuplicateFileFinderLibTests.TestUtils;
using DuplicateFileFinderLibTests.TestUtils.Fakes;

using Moq;

using Xunit;

namespace DuplicateFileFinderLibTests.Core;

public sealed class FullScanOperationTests
{
    private readonly TempFsFixture _fs = new("dff_scan_");

    [Fact]
    public async Task ExecuteAsync_RootMissing_FailsSession_AndRethrows()
    {
        var repoHost = new Mock<IRepoHost>(MockBehavior.Strict);
        var repo = new Mock<IRepoInternal>(MockBehavior.Strict);
        var session = new Mock<IScanSession>(MockBehavior.Strict);
        var fs = new Mock<IFileEnumerator>(MockBehavior.Strict);
        var hashing = new Mock<IHashingRunner<FileHashToken>>(MockBehavior.Strict);
        var volume = new Mock<IVolumeInfoProvider>(MockBehavior.Strict);

        repoHost.SetupGet(h => h.Repo).Returns(repo.Object);

        // Volume info queried (wrapped in try/catch in FullScanOperation)
        volume.Setup(v => v.GetVolumeInfoForPath(It.IsAny<string>()))
            .Returns((VolumeInfo?)null);

        // Hashing runner properties set before BeginScan
        hashing.SetupSet(h => h.ReadBufferSize = It.IsAny<int>());
        hashing.SetupSet(h => h.MaxDegreeOfParallelism = It.IsAny<int>());

        var missing = Path.Combine(Path.GetTempPath(), "dff_missing_" + Guid.NewGuid().ToString("N"));
        missing = PathUtils.NormalizePath(missing);

        var scanRoot = new ScanRoot
        {
            RootId = 1,
            RootPath = missing,
            DirId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            VolumePath = _fs.Root
        };

        var run = new ScanRun
        {
            ScanRootId = scanRoot.RootId,
            ScanSequence = 1,
            RootPath = missing,
            StartedAt = DateTimeOffset.UtcNow,
            Status = ScanRunStatus.InProgress
        };

        repo.Setup(r => r.BeginNewScanAsync(
                It.IsAny<string>(),
                It.IsAny<ScanOptions>(),
                It.IsAny<VolumeInfo?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanContext
            {
                Session = session.Object,
                ScanRoot = scanRoot,
                Run = run,
                Options = new ScanOptions()
            });

        fs.Setup(f => f.EnumerateChildren(
                It.Is<string>(p => string.Equals(PathUtils.NormalizePath(p), missing, StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .Throws(new DirectoryNotFoundException($"Directory '{missing}' not found."));

        session.SetupGet(s => s.RootDirCursor).Returns(new DirCursor(1));
        session.Setup(s => s.SetPendingDirsProvider(It.IsAny<Func<PendingDir[]>>()));

        session.Setup(s => s.BeginDirectory(It.IsAny<DirCursor>()))
            .Returns(new DirEnumerationContext(
                parentDirId: 1,
                expectedDirs: new Dictionary<string, BaseLineDirMapValue>(StringComparer.Ordinal),
                expectedFiles: new Dictionary<string, BaseLineFileMapValue>(StringComparer.Ordinal)));

        session.Setup(s => s.FailAsync(It.IsAny<string>(), cancelled: false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        session.Setup(s => s.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        var op = new FullScanOperation(repoHost.Object, fs.Object, hashing.Object, volume.Object);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            op.ExecuteAsync(missing, progress: null, CancellationToken.None));

        repo.VerifyAll();
        repoHost.VerifyAll();
        volume.VerifyAll();
        hashing.VerifyAll();

        session.Verify(s => s.FailAsync(It.IsAny<string>(), cancelled: false, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.DisposeAsync(), Times.Once);

        // New scan still enters enumeration before the directory-not-found is observed.
        session.Verify(s => s.BeginDirectory(It.IsAny<DirCursor>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_FailsCancelledTrue_AndRethrows()
    {
        var repoHost = new Mock<IRepoHost>(MockBehavior.Strict);
        var repo = new Mock<IRepoInternal>(MockBehavior.Strict);
        var session = new Mock<IScanSession>(MockBehavior.Strict);
        var fs = new Mock<IFileEnumerator>(MockBehavior.Strict);
        var hashing = new Mock<IHashingRunner<FileHashToken>>(MockBehavior.Strict);
        var volume = new Mock<IVolumeInfoProvider>(MockBehavior.Strict);

        repoHost.SetupGet(h => h.Repo).Returns(repo.Object);

        volume.Setup(v => v.GetVolumeInfoForPath(It.IsAny<string>()))
            .Returns((VolumeInfo?)null);

        hashing.SetupSet(h => h.ReadBufferSize = It.IsAny<int>());
        hashing.SetupSet(h => h.MaxDegreeOfParallelism = It.IsAny<int>());

        var scanRoot = new ScanRoot
        {
            RootId = 1,
            RootPath = _fs.Root,
            DirId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            VolumePath = _fs.Root // doesn't matter for this test
        };

        var run = new ScanRun
        {
            ScanRootId = scanRoot.RootId,
            ScanSequence = 1,
            RootPath = _fs.Root,
            StartedAt = DateTimeOffset.UtcNow,
            Status = ScanRunStatus.InProgress
        };

        repo.Setup(r => r.BeginNewScanAsync(
                It.IsAny<string>(),
                It.IsAny<ScanOptions>(),
                It.IsAny<VolumeInfo?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanContext
            {
                Session = session.Object,
                ScanRoot = scanRoot,
                Run = run,
                Options = new ScanOptions()
            });

        // Root cursor used by enumerator
        session.SetupGet(s => s.RootDirCursor).Returns(new DirCursor(1));
        session.Setup(s => s.SetPendingDirsProvider(It.IsAny<Func<PendingDir[]>>()));

        // Cancel during enumeration: easiest is enumerator throws OCE.
        fs.Setup(f => f.EnumerateChildren(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new OperationCanceledException());

        session.Setup(s => s.BeginDirectory(It.IsAny<DirCursor>()))
            .Returns(new DirEnumerationContext(
                parentDirId: 1,
                expectedDirs: new Dictionary<string, BaseLineDirMapValue>(StringComparer.Ordinal),
                expectedFiles: new Dictionary<string, BaseLineFileMapValue>(StringComparer.Ordinal)));

        session.Setup(s => s.FailAsync("Scan cancelled.", cancelled: true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        session.Setup(s => s.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        var op = new FullScanOperation(repoHost.Object, fs.Object, hashing.Object, volume.Object);

        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            op.ExecuteAsync(_fs.Root, progress: null, cts.Token));

        repo.VerifyAll();
        repoHost.VerifyAll();
        session.VerifyAll();
        fs.VerifyAll();
        volume.VerifyAll();
        hashing.VerifyAll();
    }

    [Fact]
    public async Task ExecuteAsync_WhenVolumeRotational_SetsRunnerDopAndReadBuffer()
    {
        var repoHost = new Mock<IRepoHost>(MockBehavior.Strict);
        var repo = new Mock<IRepoInternal>(MockBehavior.Strict);
        var session = new Mock<IScanSession>(MockBehavior.Strict);
        var fs = new Mock<IFileEnumerator>(MockBehavior.Strict);
        var hashing = new Mock<IHashingRunner<FileHashToken>>(MockBehavior.Strict);
        var volume = new Mock<IVolumeInfoProvider>(MockBehavior.Strict);

        repoHost.SetupGet(h => h.Repo).Returns(repo.Object);

        volume.Setup(v => v.GetVolumeInfoForPath(It.IsAny<string>()))
            .Returns((string volPath) =>
                new VolumeInfo { DevicePath = "/dev/sda", IsRotational = true, VolumePath = volPath });

        // Verify exact values (rotational => dop 1, buffer 512k)
        hashing.SetupSet(h => h.ReadBufferSize = 512 * 1024).Verifiable();
        hashing.SetupSet(h => h.MaxDegreeOfParallelism = 1).Verifiable();

        var scanRoot = new ScanRoot
        {
            RootId = 1,
            RootPath = _fs.Root,
            DirId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            VolumePath = _fs.Root // doesn't matter for this test
        };

        var run = new ScanRun
        {
            ScanRootId = scanRoot.RootId,
            ScanSequence = 1,
            RootPath = _fs.Root,
            StartedAt = DateTimeOffset.UtcNow,
            Status = ScanRunStatus.InProgress
        };

        repo.Setup(r => r.BeginNewScanAsync(It.IsAny<string>(),
                It.IsAny<ScanOptions>(),
                It.IsAny<VolumeInfo?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanContext
            {
                Session = session.Object,
                Run = run,
                ScanRoot = scanRoot,
                Options = new ScanOptions()
            });

        session.SetupGet(s => s.RootDirCursor).Returns(new DirCursor(1));

        // Empty enumeration => HashFilesAsync called with empty list
        fs.Setup(f => f.EnumerateChildren(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns([]);

        session.Setup(s => s.SetPendingDirsProvider(It.IsAny<Func<PendingDir[]>>()));

        session.Setup(s => s.BeginDirectory(It.IsAny<DirCursor>()))
            .Returns(new DirEnumerationContext(parentDirId: 1,
                expectedDirs: new Dictionary<string, BaseLineDirMapValue>(StringComparer.Ordinal),
                expectedFiles: new Dictionary<string, BaseLineFileMapValue>(StringComparer.Ordinal)));

        session.Setup(s => s.EndDirectory(ref It.Ref<DirEnumerationContext>.IsAny));

        hashing.Setup(h => h.HashFilesAsync(
                It.Is<List<FileToHash<FileHashToken>>>(l => l.Count == 0),
                It.IsAny<IProgress<DuplicateFileFinderProgressReport>?>(),
                It.IsAny<Action<FileHashToken, ReadOnlyMemory<byte>, string?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        session.Setup(s => s.CompleteAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(new ScanCompletionInfo { Generation = 1, ScanSequence = 1, ScanRootId = 1 }));

        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var op = new FullScanOperation(repoHost.Object, fs.Object, hashing.Object, volume.Object);

        await op.ExecuteAsync(_fs.Root, progress: null, CancellationToken.None);

        hashing.Verify();
        repo.VerifyAll();
        repoHost.VerifyAll();
        session.VerifyAll();
        fs.VerifyAll();
        volume.VerifyAll();
    }

    [Fact]
    public async Task ExecuteAsync_QueuesOnlyShouldHashAndNonZeroLength_AndWiresHashCallbackToSession()
    {
        var repoHost = new Mock<IRepoHost>(MockBehavior.Strict);
        var repo = new Mock<IRepoInternal>(MockBehavior.Strict);
        var fs = new Mock<IFileEnumerator>(MockBehavior.Strict);
        var hashing = new Mock<IHashingRunner<FileHashToken>>(MockBehavior.Strict);
        var volume = new Mock<IVolumeInfoProvider>(MockBehavior.Strict);

        repoHost.SetupGet(h => h.Repo).Returns(repo.Object);

        volume.Setup(v => v.GetVolumeInfoForPath(It.IsAny<string>()))
            .Returns((VolumeInfo?)null);

        // Non-rotational defaults (we don't assert exact dop/buffer here)
        hashing.SetupSet(h => h.ReadBufferSize = It.IsAny<int>());
        hashing.SetupSet(h => h.MaxDegreeOfParallelism = It.IsAny<int>());

        var session = new CapturingScanSession();
        session.SetRootDirId(50);

        // Decisions: only ten.bin should hash. (zero.bin size==0 is filtered by FullScanOperation anyway)
        session.FileDecisionsByName["zero.bin"] = true;
        session.FileDecisionsByName["ten.bin"] = true;
        session.FileDecisionsByName["skip.bin"] = false;

        var scanRoot = new ScanRoot
        {
            RootId = 1,
            RootPath = _fs.Root,
            DirId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            VolumePath = _fs.Root // doesn't matter for this test
        };

        var run = new ScanRun
        {
            ScanRootId = scanRoot.RootId,
            ScanSequence = 1,
            RootPath = _fs.Root,
            StartedAt = DateTimeOffset.UtcNow,
            Status = ScanRunStatus.InProgress
        };

        repo.Setup(r => r.BeginNewScanAsync(It.IsAny<string>(),
                It.IsAny<ScanOptions>(),
                It.IsAny<VolumeInfo?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanContext
            {
                Session = session,
                Run = run,
                ScanRoot = scanRoot,
                Options = new ScanOptions()
            });

        var normRoot = PathUtils.NormalizePath(_fs.Root);

        var entries = new[]
        {
            // size==0 => never queued even if ShouldHash == true
            new FsEntry(false, Path.Combine(normRoot, "zero.bin"), "zero.bin", 0, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch),
            // should hash
            new FsEntry(false, Path.Combine(normRoot, "ten.bin"), "ten.bin", 10, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch),
            // should NOT hash (decision)
            new FsEntry(false, Path.Combine(normRoot, "skip.bin"), "skip.bin", 10, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch),
        };

        fs.Setup(f => f.EnumerateChildren(
                It.Is<string>(p => PathUtils.NormalizePath(p) == normRoot),
                It.IsAny<CancellationToken>()))
            .Returns(entries);

        List<FileToHash<FileHashToken>>? captured = null;

        hashing.Setup(h => h.HashFilesAsync(
                It.IsAny<List<FileToHash<FileHashToken>>>(),
                It.IsAny<IProgress<DuplicateFileFinderProgressReport>?>(),
                It.IsAny<Action<FileHashToken, ReadOnlyMemory<byte>, string?>>(),
                It.IsAny<CancellationToken>()))
            .Callback<List<FileToHash<FileHashToken>>, IProgress<DuplicateFileFinderProgressReport>?,
                Action<FileHashToken, ReadOnlyMemory<byte>, string?>, CancellationToken>((list, _, onFileHashed, _) =>
            {
                captured = list;

                // simulate hashing success for queued files
                foreach (var item in list)
                {
                    var bytes = new byte[16];
                    bytes[0] = 1;
                    onFileHashed(item.Token, bytes, null);
                }
            })
            .Returns(Task.CompletedTask);

        var op = new FullScanOperation(repoHost.Object, fs.Object, hashing.Object, volume.Object);

        await op.ExecuteAsync(normRoot, progress: null, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Single(captured!); // only ten.bin should be queued
        Assert.Equal(PathUtils.NormalizePath(Path.Combine(normRoot, "ten.bin")), captured![0].FullPath);
        Assert.Equal("ten.bin", captured![0].Token.Name);

        Assert.Single(session.HashCompletions);
        Assert.Equal("ten.bin", session.HashCompletions[0].token.Name);
        Assert.Null(session.HashCompletions[0].errorMessage);

        Assert.False(session.LastFailCancelled);
        Assert.Null(session.LastFailMessage);

        repo.VerifyAll();
        repoHost.VerifyAll();
        fs.VerifyAll();
        volume.VerifyAll();
        hashing.VerifyAll();
    }

    [Fact]
    public async Task ExecuteAsync_ByScanRootId_CallsBeginRescanAsyncOverloadAndCompletes()
    {
        var repoHost = new Mock<IRepoHost>(MockBehavior.Strict);
        var repo = new Mock<IRepoInternal>(MockBehavior.Strict);
        var session = new Mock<IScanSession>(MockBehavior.Strict);
        var fs = new Mock<IFileEnumerator>(MockBehavior.Strict);
        var hashing = new Mock<IHashingRunner<FileHashToken>>(MockBehavior.Strict);
        var volume = new Mock<IVolumeInfoProvider>(MockBehavior.Strict);

        repoHost.SetupGet(h => h.Repo).Returns(repo.Object);

        // FullScanOperation probes using ResolveScanRootPath(scanRoot)
        var volPath = PathUtils.NormalizePath(_fs.Dir("vol"));
        var rootDir = PathUtils.NormalizePath(Path.Combine(volPath, "root"));
        Directory.CreateDirectory(rootDir);

        var scanRoot = new ScanRoot
        {
            RootId = 123,
            RootPath = "root",
            DirId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            VolumePath = volPath
        };

        repo.SetupGet(r => r.ScanRootsView).Returns([scanRoot]);

        volume.Setup(v => v.GetVolumeInfoForPath(It.IsAny<string>())).Returns((VolumeInfo?)null);

        hashing.SetupSet(h => h.ReadBufferSize = It.IsAny<int>());
        hashing.SetupSet(h => h.MaxDegreeOfParallelism = It.IsAny<int>());

        repo.Setup(r => r.BeginRescanAsync(
                It.Is<ScanRootId>(id => id == 123),
                It.IsAny<ScanOptions>(),
                It.IsAny<VolumeInfo?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanContext
            {
                Session = session.Object,
                ScanRoot = scanRoot,
                Run = new ScanRun
                {
                    ScanRootId = 123,
                    ScanSequence = 1,
                    RootPath = rootDir,
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = ScanRunStatus.InProgress
                },
                Options = new ScanOptions(StartFresh: true)
            });

        session.SetupGet(s => s.RootDirCursor).Returns(new DirCursor(1));
        session.Setup(s => s.SetPendingDirsProvider(It.IsAny<Func<PendingDir[]>>()));

        fs.Setup(f => f.EnumerateChildren(
                It.Is<string>(p => PathUtils.NormalizePath(p) == rootDir),
                It.IsAny<CancellationToken>()))
                .Returns([]);

        session.Setup(s => s.BeginDirectory(It.IsAny<DirCursor>()))
            .Returns(new DirEnumerationContext(
                parentDirId: 1,
                expectedDirs: new Dictionary<string, BaseLineDirMapValue>(StringComparer.Ordinal),
                expectedFiles: new Dictionary<string, BaseLineFileMapValue>(StringComparer.Ordinal)));

        session.Setup(s => s.EndDirectory(ref It.Ref<DirEnumerationContext>.IsAny));

        hashing.Setup(h => h.HashFilesAsync(
                It.Is<List<FileToHash<FileHashToken>>>(l => l.Count == 0),
                It.IsAny<IProgress<DuplicateFileFinderProgressReport>?>(),
                It.IsAny<Action<FileHashToken, ReadOnlyMemory<byte>, string?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        session.Setup(s => s.CompleteAsync(It.IsAny<CancellationToken>())).Returns(
            Task.FromResult(new ScanCompletionInfo { Generation = 1, ScanSequence = 1, ScanRootId = 123 }));
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var op = new FullScanOperation(repoHost.Object, fs.Object, hashing.Object, volume.Object);

        await op.ExecuteAsync(scanRootId: 123, progress: null, ct: CancellationToken.None);

        repo.VerifyAll();
        repoHost.VerifyAll();
        session.VerifyAll();
        fs.VerifyAll();
        volume.VerifyAll();
        hashing.VerifyAll();
    }

    [Fact]
    public async Task ExecuteAsync_ByDirHandle_ForcesStartFresh_AndEnumeratesOnlyThatFolder()
    {
        var repoHost = new Mock<IRepoHost>(MockBehavior.Strict);
        var repo = new Mock<IRepoInternal>(MockBehavior.Strict);
        var session = new Mock<IScanSession>(MockBehavior.Strict);
        var fs = new Mock<IFileEnumerator>(MockBehavior.Strict);
        var hashing = new Mock<IHashingRunner<FileHashToken>>(MockBehavior.Strict);
        var volume = new Mock<IVolumeInfoProvider>(MockBehavior.Strict);

        repoHost.SetupGet(h => h.Repo).Returns(repo.Object);

        // Arrange real folder paths that match ResolveScanRootPath(scanRoot)
        var volPath = PathUtils.NormalizePath(_fs.Dir("vol"));
        var root = PathUtils.NormalizePath(Path.Combine(volPath, "root"));
        var sub = PathUtils.NormalizePath(Path.Combine(root, "sub"));
        Directory.CreateDirectory(sub);

        var scanRoot = new ScanRoot
        {
            RootId = 7,
            RootPath = "root",
            DirId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            VolumePath = volPath
        };
        repo.SetupGet(r => r.ScanRootsView).Returns([scanRoot]);

        var snap = new ScanRootSnapshotView
        {
            ScanRootId = 7,
            StringPool = PackedStringPool.FromStrings(["", "sub"]),
            Dirs =
            [
            new DirRecordV2 { DirId = 1, ParentDirId = -1, NameStrIdx = 0, ErrorMessageStrIdx = 0, LastSeenScanSequence = 1, Status = ScanEntryStatus.Enumerated },
            new DirRecordV2 { DirId = 2, ParentDirId = 1, NameStrIdx = 1, ErrorMessageStrIdx = 0, LastSeenScanSequence = 1, Status = ScanEntryStatus.Enumerated }
            ],
            Files = []
        };
        repo.Setup(r => r.TryGetScanRootView(7)).Returns(snap);

        volume.Setup(v => v.GetVolumeInfoForPath(It.IsAny<string>())).Returns((VolumeInfo?)null);
        hashing.SetupSet(h => h.ReadBufferSize = It.IsAny<int>());
        hashing.SetupSet(h => h.MaxDegreeOfParallelism = It.IsAny<int>());

        ScanOptions? capturedOptions = null;
        repo.Setup(r => r.BeginSubtreeScanAsync(
                It.Is<ScanRootId>(id => id == 7),
                It.IsAny<ScanOptions>(),
                It.IsAny<VolumeInfo?>(),
                It.IsAny<CancellationToken>()))
            .Callback<ScanRootId, ScanOptions, VolumeInfo?, CancellationToken>((_, o, _, _) => capturedOptions = o)
            .ReturnsAsync(new ScanContext
            {
                Session = session.Object,
                ScanRoot = scanRoot,
                Run = new ScanRun
                {
                    ScanRootId = 7,
                    ScanSequence = 1,
                    RootPath = root,
                    StartedAt = DateTimeOffset.UtcNow,
                    Status = ScanRunStatus.InProgress
                },
                Options = new ScanOptions(StartFresh: true)
            });

        session.SetupGet(s => s.RootDirCursor).Returns(new DirCursor(1));
        session.Setup(s => s.SetPendingDirsProvider(It.IsAny<Func<PendingDir[]>>()));

        fs.Setup(f => f.EnumerateChildren(
            It.Is<string>(p => PathUtils.NormalizePath(p) == sub),
                It.IsAny<CancellationToken>()))
            .Returns([]);

        session.Setup(s => s.BeginDirectory(It.IsAny<DirCursor>()))
            .Returns(new DirEnumerationContext(
                parentDirId: 2,
                expectedDirs: new Dictionary<string, BaseLineDirMapValue>(StringComparer.Ordinal),
                expectedFiles: new Dictionary<string, BaseLineFileMapValue>(StringComparer.Ordinal)));
        session.Setup(s => s.EndDirectory(ref It.Ref<DirEnumerationContext>.IsAny));

        hashing.Setup(h => h.HashFilesAsync(
                It.Is<List<FileToHash<FileHashToken>>>(l => l.Count == 0),
                It.IsAny<IProgress<DuplicateFileFinderProgressReport>?>(),
                It.IsAny<Action<FileHashToken, ReadOnlyMemory<byte>, string?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        session.Setup(s => s.CompleteAsync(It.IsAny<CancellationToken>())).Returns(
            Task.FromResult(new ScanCompletionInfo { Generation = 1, ScanRootId = 1, ScanSequence = 1 }));
        session.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var op = new FullScanOperation(repoHost.Object, fs.Object, hashing.Object, volume.Object);

        await op.ExecuteAsync(new DirHandle(ScanRootId: 7, Index: 1), progress: null, ct: CancellationToken.None);

        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions!.Value.StartFresh);

        repo.VerifyAll();
        repoHost.VerifyAll();
        session.VerifyAll();
        fs.VerifyAll();
        volume.VerifyAll();
        hashing.VerifyAll();

        session.Verify(s => s.FailAsync(
            It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);

        fs.Verify(f => f.EnumerateChildren(
            It.Is<string>(p => PathUtils.NormalizePath(p) == root),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
