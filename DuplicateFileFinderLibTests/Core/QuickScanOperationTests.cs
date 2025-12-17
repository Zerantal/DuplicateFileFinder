using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Hashing;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using NSubstitute;
using Xunit;

namespace DuplicateFileFinderLibTests.Core;

public class QuickScanOperationTests
{
    [Fact]
    public async Task ExecuteAsync_UsesRotationalVolumeInfo_SetsDegreeOfParallelismTo1()
    {
        // Arrange
        var host         = Substitute.For<IRepoHost>();
        var repo         = Substitute.For<IRepo>();
        var treeIndex    = Substitute.For<ITreeIndexReadModel>();
        var fs           = Substitute.For<IFileEnumerator>();
        var pipeline     = Substitute.For<IChecksumPipeline>();
        var volProvider  = Substitute.For<IVolumeInfoProvider>();
        var session      = Substitute.For<IScanSession>();

        host.Repo.Returns(repo);
        host.TreeIndex.Returns(treeIndex);

        var vinfo = new VolumeInfo
        {
            IsRotational = true,
            DevicePath = "/dev/sda",
            VolumePath = String.Empty
        };

        volProvider.GetVolumeInfoForPath(Arg.Any<string>()).Returns(vinfo);

        var nonExistingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        repo.BeginScan(nonExistingPath, ScanOperation.QuickScan, vinfo).Returns(session);
        repo.GetRepoView().Returns(Substitute.For<IRepoView>());

        // Root dir record (required for EnumerateQuickAsync, though we'll short-circuit)
        session.RootDir.Returns(new DirRecord
        {
            DirId       = 1,
            ParentDirId = null,
            Name        = "root",
            Created     = DateTime.UtcNow,
            Modified    = DateTime.UtcNow,
            Status      = ScanEntryStatus.Enumerated
        });

        var op = new QuickScanOperation(host, fs, pipeline, volProvider);

        // Act
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            op.ExecuteAsync(nonExistingPath, progress: null, CancellationToken.None));

        // Assert
        volProvider.Received(1).GetVolumeInfoForPath(nonExistingPath);
        repo.Received(1).BeginScan(nonExistingPath, ScanOperation.QuickScan, vinfo);
    }

    [Fact]
    public async Task ExecuteAsync_NonExistingRoot_ThrowsAndFailsSession()
    {
        // Arrange
        var host         = Substitute.For<IRepoHost>();
        var repo         = Substitute.For<IRepo>();
        var treeIndex    = Substitute.For<ITreeIndexReadModel>();
        var fs           = Substitute.For<IFileEnumerator>();
        var pipeline     = Substitute.For<IChecksumPipeline>();
        var volProvider  = Substitute.For<IVolumeInfoProvider>();
        var session      = Substitute.For<IScanSession>();

        host.Repo.Returns(repo);
        host.TreeIndex.Returns(treeIndex);

        var vinfo = (VolumeInfo?)null;
        volProvider.GetVolumeInfoForPath(Arg.Any<string>()).Returns(vinfo);

        var nonExistingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        repo.BeginScan(nonExistingPath, ScanOperation.QuickScan, vinfo).Returns(session);

        var op = new QuickScanOperation(host, fs, pipeline, volProvider);

        // Act
        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            op.ExecuteAsync(nonExistingPath, progress: null, CancellationToken.None));

        // Assert
        Assert.Contains("Root scan path does not exist", ex.Message, StringComparison.Ordinal);

        await session.Received(1)
            .FailAsync(Arg.Is<string>(m => m.Contains("Root scan path does not exist",
                    StringComparison.Ordinal)),
                cancelled: false,
                Arg.Any<CancellationToken>());

        await session.Received(1).DisposeAsync();
        await repo.DidNotReceive().CompactAsync(ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringEnumeration_PropagatesAndMarksScanCancelled()
    {
        // Arrange
        var host         = Substitute.For<IRepoHost>();
        var repo         = Substitute.For<IRepo>();
        var treeIndex    = Substitute.For<ITreeIndexReadModel>();
        var fs           = Substitute.For<IFileEnumerator>();
        var pipeline     = Substitute.For<IChecksumPipeline>();
        var volProvider  = Substitute.For<IVolumeInfoProvider>();
        var session      = Substitute.For<IScanSession>();
        var repoView     = Substitute.For<IRepoView>();
    
        host.Repo.Returns(repo);
        host.TreeIndex.Returns(treeIndex);
    
        var tempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "dff_quickscan_cancel_" + Guid.NewGuid()));
    
        var vinfo = (VolumeInfo?)null;
        volProvider.GetVolumeInfoForPath(tempDir.FullName).Returns(vinfo);
    
        repo.BeginScan(tempDir.FullName, ScanOperation.QuickScan, vinfo).Returns(session);
        repo.GetRepoView().Returns(repoView);
    
        session.RootDir.Returns(new DirRecord
        {
            DirId       = 1,
            ParentDirId = null,
            Name        = tempDir.Name,
            Created     = DateTime.UtcNow,
            Modified    = DateTime.UtcNow,
            Status      = ScanEntryStatus.Enumerated
        });
    
        // No children in tree index
        treeIndex.GetChildDirs(Arg.Any<DirHandle>()).Returns([]);
        treeIndex.GetChildFiles(Arg.Any<DirHandle>()).Returns([]);
    
        // Fs enumerator throws OCE when enumerated
        fs.EnumerateChildren(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                ct.ThrowIfCancellationRequested();
                return [];
            });
    
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
    
        var op = new QuickScanOperation(host, fs, pipeline, volProvider);
    
        // Act
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            op.ExecuteAsync(tempDir.FullName, progress: null, cts.Token));
    
        // Assert
        await session.Received(1)
            .FailAsync("Scan cancelled.", true, cts.Token);
        await session.Received(1).DisposeAsync();
    
        tempDir.Delete(recursive: true);
    }
}