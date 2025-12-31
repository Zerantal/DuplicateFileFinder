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

        repo.Setup(r => r.BeginScanAsync(It.IsAny<string>(),
                It.IsAny<ScanOptions>(),
                It.IsAny<VolumeInfo?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanContext
            {
                Session = session.Object,
                ScanRoot = null!,
                Run = null!,
                Options = new ScanOptions()
            });

        session.SetupGet(s => s.RootDirCursor).Returns(new DirCursor(1));

        session.Setup(s => s.SetPendingDirsProvider(It.IsAny<Func<PendingDir[]>>()));

        session.Setup(s => s.FailAsync(
                It.Is<string>(m => m.Contains("does not exist", StringComparison.OrdinalIgnoreCase)),
                cancelled: false,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        session.Setup(s => s.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        var op = new FullScanOperation(repoHost.Object, fs.Object, hashing.Object, volume.Object);

        var missing = Path.Combine(Path.GetTempPath(), "dff_missing_" + Guid.NewGuid().ToString("N"));
        missing = PathUtils.NormalizePath(missing);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            op.ExecuteAsync(missing, progress: null, CancellationToken.None));

        repo.VerifyAll();
        repoHost.VerifyAll();
        session.VerifyAll();
        volume.VerifyAll();
        hashing.VerifyAll();
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

        repo.Setup(r => r.BeginScanAsync(It.IsAny<string>(),
                It.IsAny<ScanOptions>(),
                It.IsAny<VolumeInfo?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanContext
            {
                Session = session.Object,
                ScanRoot = null!,
                Run = null!,
                Options = new ScanOptions()
            });

        // Root cursor used by enumerator
        session.SetupGet(s => s.RootDirCursor).Returns(new DirCursor(1));

        // Cancel during enumeration: easiest is enumerator throws OCE.
        fs.Setup(f => f.EnumerateChildren(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new OperationCanceledException());

        session.Setup(s => s.BeginDirectory(It.IsAny<DirCursor>()))
            .Returns(new DirEnumerationContext(parentDirId: 1,
                expectedDirs: new Dictionary<string, (long, string, ScanEntryStatus, long)>(StringComparer.Ordinal),
                expectedFiles: new Dictionary<string, (long, string, ScanEntryStatus, long)>(StringComparer.Ordinal)));

        session.Setup(s => s.FailAsync("Scan cancelled.", cancelled: true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        session.Setup(s => s.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        session.Setup(s => s.SetPendingDirsProvider(It.IsAny<Func<PendingDir[]>>()));

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
            .Returns((string volPath) => new VolumeInfo { DevicePath = "/dev/sda", IsRotational = true, VolumePath = volPath });

        // Verify exact values (rotational => dop 1, buffer 512k)
        hashing.SetupSet(h => h.ReadBufferSize = 512 * 1024).Verifiable();
        hashing.SetupSet(h => h.MaxDegreeOfParallelism = 1).Verifiable();

        repo.Setup(r => r.BeginScanAsync(It.IsAny<string>(),
                It.IsAny<ScanOptions>(),
                It.IsAny<VolumeInfo?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanContext { Session = session.Object, Run = null!, ScanRoot = null!, Options = new ScanOptions() });

        session.SetupGet(s => s.RootDirCursor).Returns(new DirCursor(1));

        // Empty enumeration => HashFilesAsync called with empty list
        fs.Setup(f => f.EnumerateChildren(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Array.Empty<FsEntry>());

        session.Setup(s => s.SetPendingDirsProvider(It.IsAny<Func<PendingDir[]>>()));

        session.Setup(s => s.BeginDirectory(It.IsAny<DirCursor>()))
            .Returns(new DirEnumerationContext(parentDirId: 1,
                expectedDirs: new Dictionary<string, (long, string, ScanEntryStatus, long)>(StringComparer.Ordinal),
                expectedFiles: new Dictionary<string, (long, string, ScanEntryStatus, long)>(StringComparer.Ordinal)));

        session.Setup(s => s.EndDirectory(ref It.Ref<DirEnumerationContext>.IsAny));

        hashing.Setup(h => h.HashFilesAsync(
                It.Is<List<FileToHash<FileHashToken>>>(l => l.Count == 0),
                It.IsAny<IProgress<DuplicateFileFinderProgressReport>?>(),
                It.IsAny<Action<FileHashToken, ReadOnlyMemory<byte>, string?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        session.Setup(s => s.CompleteAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

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

        repo.Setup(r => r.BeginScanAsync(It.IsAny<string>(),
                It.IsAny<ScanOptions>(),
                It.IsAny<VolumeInfo?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanContext { Session = session, Run = null!, ScanRoot = null!, Options = new ScanOptions() });

        var normRoot = PathUtils.NormalizePath(_fs.Root);

        var entries = new[]
        {
            // size==0 => never queued even if ShouldHash == true
            new FsEntry(false, Path.Combine(normRoot, "zero.bin"), "zero.bin", 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            // should hash
            new FsEntry(false, Path.Combine(normRoot, "ten.bin"), "ten.bin", 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            // should NOT hash (decision)
            new FsEntry(false, Path.Combine(normRoot, "skip.bin"), "skip.bin", 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
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
            .Callback<List<FileToHash<FileHashToken>>, IProgress<DuplicateFileFinderProgressReport>?, Action<FileHashToken, ReadOnlyMemory<byte>, string?>, CancellationToken>(
                (list, _, onFileHashed, _) =>
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
}
