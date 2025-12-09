using System;
using System.Linq;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using Moq;
using Xunit;

namespace DuplicateFileFinderLibTests.Core
{
    public sealed class RemoveRootOperationTests
    {
        [Fact]
        public void Execute_WhenScanRootDoesNotExist_DoesNothing()
        {
            // Arrange
            var repoMock = new Mock<IRepo>(MockBehavior.Strict);

            repoMock
                .SetupGet(r => r.ScanRootsView)
                .Returns([]);
            
            var repoInternalMock = repoMock.As<IRepoInternal>();

            // No AllocateRunId / CommitDelta expected
            var treeIndexMock = new Mock<ITreeIndexReadModel>(MockBehavior.Strict);

            var hostMock = new Mock<IRepoHost>(MockBehavior.Strict);
            hostMock.SetupGet(h => h.Repo).Returns(repoMock.Object);
            hostMock.SetupGet(h => h.TreeIndex).Returns(treeIndexMock.Object);

            var op = new RemoveRootOperation(hostMock.Object);

            // Act
            op.Execute(scanRootId: 1234);

            // Assert
            repoInternalMock.Verify(r => r.AllocateRunId(), Times.Never);
            repoMock.Verify(r => r.CommitDelta(It.IsAny<RepoDelta>()), Times.Never);
        }

        [Fact]
        public void Execute_RootWithNoChildren_EmitsSingleDirTombstone_AndSoftDeletesRoot()
        {
            // Arrange
            const long rootId   = 10;
            const long rootDirId = 100;

            var scanRoot = new ScanRoot
            {
                RootId        = rootId,
                RootPath      = "/data",
                DirId         = rootDirId,
                CreatedAt     = DateTimeOffset.UtcNow,
                LastScannedAt = DateTimeOffset.UtcNow,
                VolumeId      = null,
                VolumeLabel   = null,
                IsRotational  = null,
                FileSystemType = null,
                DevicePath    = null,
                DeviceModel   = null,
                IsDeleted     = false,
                DeletedAtUtc  = null
            };

            var repoMock = new Mock<IRepo>(MockBehavior.Strict);

            repoMock
                .SetupGet(r => r.ScanRootsView)
                .Returns([scanRoot]);


            RepoDelta? committedDelta = null;
            repoMock
                .Setup(r => r.CommitDelta(It.IsAny<RepoDelta>()))
                .Callback<RepoDelta>(d => committedDelta = d);

            const long allocatedSeq = 42;
            var repoInternalMock = repoMock.As<IRepoInternal>();
            repoInternalMock
                .Setup(r => r.AllocateRunId())
                .Returns(allocatedSeq);
            
            repoInternalMock
                .Setup(r => r.DeleteScanRoot(rootId));
            
            // Tree index: no children, no files
            var treeIndexMock = new Mock<ITreeIndexReadModel>(MockBehavior.Strict);

            treeIndexMock
                .Setup(t => t.GetChildDirIds(rootDirId))
                .Returns([]);

            treeIndexMock
                .Setup(t => t.GetChildFileIds(rootDirId))
                .Returns([]);

            var hostMock = new Mock<IRepoHost>(MockBehavior.Strict);
            hostMock.SetupGet(h => h.Repo).Returns(repoMock.Object);
            hostMock.SetupGet(h => h.TreeIndex).Returns(treeIndexMock.Object);

            var op = new RemoveRootOperation(hostMock.Object);

            // Act
            op.Execute(rootId);

            // Assert
            repoInternalMock.Verify(r => r.AllocateRunId(), Times.Once);
            repoMock.Verify(r => r.CommitDelta(It.IsAny<RepoDelta>()), Times.Once);

            Assert.NotNull(committedDelta);
            Assert.Equal(allocatedSeq, committedDelta!.ScanSequence);

            var dir = Assert.Single(committedDelta.Dirs);
            Assert.Equal(rootDirId, dir.DirId);
            Assert.Equal(ScanEntryStatus.Deleted, dir.Status);

            Assert.Empty(committedDelta.Files);
        }

        [Fact]
        public void Execute_ComplexTree_TraversesAllDirsAndFiles()
        {
            // Tree:
            //
            //  rootDir(10)
            //   ├─ childA(11)
            //   │   └─ grandchild(13)
            //   │       ├─ f130
            //   │       └─ f131
            //   └─ childB(12)
            //
            // Files:
            //   dir 10: f100, f101
            //   dir 11: f110
            //   dir 12: (none)
            //   dir 13: f130, f131

            const long rootId    = 1;
            const long rootDirId = 10;
            const long childA    = 11;
            const long childB    = 12;
            const long grandchild = 13;

            var scanRoot = new ScanRoot
            {
                RootId        = rootId,
                RootPath      = "/root",
                DirId         = rootDirId,
                CreatedAt     = DateTimeOffset.UtcNow,
                LastScannedAt = DateTimeOffset.UtcNow,
                VolumeId      = null,
                VolumeLabel   = null,
                IsRotational  = null,
                FileSystemType = null,
                DevicePath    = null,
                DeviceModel   = null,
                IsDeleted     = false,
                DeletedAtUtc  = null
            };

            var repoMock = new Mock<IRepo>(MockBehavior.Strict);
            repoMock
                .SetupGet(r => r.ScanRootsView)
                .Returns([scanRoot]);
            
            RepoDelta? committed = null;
            repoMock
                .Setup(r => r.CommitDelta(It.IsAny<RepoDelta>()))
                .Callback<RepoDelta>(d => committed = d);
            
            const long seq = 999;
            var repoInternalMock =  repoMock.As<IRepoInternal>();
            repoInternalMock
                .Setup(r => r.AllocateRunId())
                .Returns(seq);
            
            repoInternalMock
                .Setup(r => r.DeleteScanRoot(rootId));

            // Tree index wiring
            var treeIndexMock = new Mock<ITreeIndexReadModel>(MockBehavior.Strict);

            // Children dirs
            treeIndexMock
                .Setup(t => t.GetChildDirIds(rootDirId))
                .Returns([childA, childB]);

            treeIndexMock
                .Setup(t => t.GetChildDirIds(childA))
                .Returns([grandchild]);

            treeIndexMock
                .Setup(t => t.GetChildDirIds(childB))
                .Returns([]);

            treeIndexMock
                .Setup(t => t.GetChildDirIds(grandchild))
                .Returns([]);

            // Children files
            treeIndexMock
                .Setup(t => t.GetChildFileIds(rootDirId))
                .Returns([100L, 101L]);
            treeIndexMock
                .Setup(t => t.GetChildFileIds(childA))
                .Returns([110L]);
            treeIndexMock
                .Setup(t => t.GetChildFileIds(childB))
                .Returns([]);
            treeIndexMock
                .Setup(t => t.GetChildFileIds(grandchild))
                .Returns([130L, 131L]);

            var hostMock = new Mock<IRepoHost>(MockBehavior.Strict);
            hostMock.SetupGet(h => h.Repo).Returns(repoMock.Object);
            hostMock.SetupGet(h => h.TreeIndex).Returns(treeIndexMock.Object);

            var op = new RemoveRootOperation(hostMock.Object);

            // Act
            op.Execute(rootId);

            // Assert
            repoInternalMock.Verify(r => r.AllocateRunId(), Times.Once);
            repoMock.Verify(r => r.CommitDelta(It.IsAny<RepoDelta>()), Times.Once);
            repoInternalMock.Verify(r => r.DeleteScanRoot(rootId), Times.Once);

            Assert.NotNull(committed);
            Assert.Equal(seq, committed!.ScanSequence);

            // Dirs: root + 3 children
            var dirIds = committed.Dirs.Select(d => d.DirId).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { rootDirId, childA, childB, grandchild }, dirIds);
            Assert.All(committed.Dirs, d => Assert.Equal(ScanEntryStatus.Deleted, d.Status));

            // Files: 5 entries
            var fileIds = committed.Files.Select(f => f.FileId).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 100L, 101L, 110L, 130L, 131L }, fileIds);
            Assert.All(committed.Files, f => Assert.Equal(ScanEntryStatus.Deleted, f.Status));
        }

        [Fact]
        public void Execute_RootHasFilesOnly_DeletesFilesAndRootDir_AndSoftDeletesRoot()
        {
            // Arrange
            const long rootId    = 5;
            const long rootDirId = 50;
            const long seq       = 7;

            var scanRoot = new ScanRoot
            {
                RootId        = rootId,
                RootPath      = "/data",
                DirId         = rootDirId,
                CreatedAt     = DateTimeOffset.UtcNow,
                LastScannedAt = DateTimeOffset.UtcNow,
                VolumeId      = null,
                VolumeLabel   = null,
                IsRotational  = null,
                FileSystemType = null,
                DevicePath    = null,
                DeviceModel   = null,
                IsDeleted     = false,
                DeletedAtUtc  = null
            };

            var repoMock = new Mock<IRepo>(MockBehavior.Strict);

            repoMock
                .SetupGet(r => r.ScanRootsView)
                .Returns([scanRoot]);

            RepoDelta? committed = null;
            repoMock
                .Setup(r => r.CommitDelta(It.IsAny<RepoDelta>()))
                .Callback<RepoDelta>(d => committed = d);
            
            var repoInternalMock = repoMock.As<IRepoInternal>();
            repoInternalMock
                .Setup(r => r.AllocateRunId())
                .Returns(seq);
            
            repoInternalMock
                .Setup(r => r.DeleteScanRoot(rootId));

            var treeIndexMock = new Mock<ITreeIndexReadModel>(MockBehavior.Strict);

            treeIndexMock
                .Setup(t => t.GetChildDirIds(rootDirId))
                .Returns([]);

            treeIndexMock
                .Setup(t => t.GetChildFileIds(rootDirId))
                .Returns([1L, 2L, 3L]);

            var hostMock = new Mock<IRepoHost>(MockBehavior.Strict);
            hostMock.SetupGet(h => h.Repo).Returns(repoMock.Object);
            hostMock.SetupGet(h => h.TreeIndex).Returns(treeIndexMock.Object);

            var op = new RemoveRootOperation(hostMock.Object);

            // Act
            op.Execute(rootId);

            // Assert
            repoInternalMock.Verify(r => r.AllocateRunId(), Times.Once);
            repoMock.Verify(r => r.CommitDelta(It.IsAny<RepoDelta>()), Times.Once);
            repoInternalMock.Verify(r => r.DeleteScanRoot(rootId), Times.Once);

            Assert.NotNull(committed);
            Assert.Equal(seq, committed!.ScanSequence);

            var dir = Assert.Single(committed.Dirs);
            Assert.Equal(rootDirId, dir.DirId);
            Assert.Equal(ScanEntryStatus.Deleted, dir.Status);

            var fileIds = committed.Files.Select(f => f.FileId).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 1L, 2L, 3L }, fileIds);
            Assert.All(committed.Files, f => Assert.Equal(ScanEntryStatus.Deleted, f.Status));
        }
    }
}
