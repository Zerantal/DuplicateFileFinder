using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Util;
using DuplicateFileFinder.GuiTests.Features.Duplicates.TestSupport;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;

using Moq;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates.TreeMap;

public sealed class TreeMapSelectionSyncTests
{
    [Fact]
    public void SelectingDirectoryNode_PublishesDirectorySelectionToSharedContext()
    {
        var snapshot = DuplicatesSelectionTestHelpers.BuildSnapshot(
            scanRootId: 1,
            dirs:
            [
                new(100, -1, "root"),
                new(200, 100, "A")
            ],
            files:
            [
                new(700, 200, "f.txt", 123)
            ]);

        var repo = new FakeRepo(snapshot.ScanRoots.Values) { SnapshotToReturn = snapshot };
        var fileDir = new FakeFileDirReadModel();
        DuplicatesSelectionTestHelpers.SeedFileDir(fileDir, snapshot);

        var treeIndex = DuplicatesSelectionTestHelpers.BuildTreeIndex(snapshot);

        var host = new Mock<IRepoHost>(MockBehavior.Strict);
        host.SetupGet(x => x.Repo).Returns(repo);
        host.SetupGet(x => x.FileDirIndex).Returns(fileDir);
        host.SetupGet(x => x.TreeIndex).Returns(treeIndex);
        host.SetupGet(x => x.HashIndex).Returns(DuplicatesSelectionTestHelpers.BuildEmptyHashIndex());

        var selectionContext = new DuplicateExplorerSelectionContext();

        var vm = new TreeMapController(host.Object, selectionContext, new DisposableManager())
        {
            Metric = TreeMapMetric.TotalFiles,
            Options = new TreeMapBuildOptions
            {
                MaxDepth = 8,
                MaxSubdirsPerDir = 64,
                MaxFilesPerDir = 64,
                MaxTotalFileNodes = 1024
            }
        };

        vm.Rebuild(snapshot);

        Assert.True(DuplicatesSelectionTestHelpers.TryGetDirHandle(snapshot, 1, 200, out var dir200));
        vm.SelectedNode = vm.DirNodeByHandle[dir200];

        var current = Assert.IsType<DuplicateExplorerSelectionContext.SelectionTarget>(selectionContext.Current);
        Assert.Equal(DuplicateExplorerSelectionContext.SelectionKind.Directory, current.Kind);
        Assert.Equal(200, current.DirId);
        Assert.Equal(100, current.ParentDirId);
    }

    [Fact]
    public void SelectingFileNode_PublishesFileSelectionToSharedContext()
    {
        var snapshot = DuplicatesSelectionTestHelpers.BuildSnapshot(
            scanRootId: 1,
            dirs:
            [
                new(100, -1, "root"),
                new(200, 100, "A")
            ],
            files:
            [
                new(700, 200, "f.txt", 123)
            ]);

        var repo = new FakeRepo(snapshot.ScanRoots.Values) { SnapshotToReturn = snapshot };
        var fileDir = new FakeFileDirReadModel();
        DuplicatesSelectionTestHelpers.SeedFileDir(fileDir, snapshot);

        var treeIndex = DuplicatesSelectionTestHelpers.BuildTreeIndex(snapshot);

        var host = new Mock<IRepoHost>(MockBehavior.Strict);
        host.SetupGet(x => x.Repo).Returns(repo);
        host.SetupGet(x => x.FileDirIndex).Returns(fileDir);
        host.SetupGet(x => x.TreeIndex).Returns(treeIndex);
        host.SetupGet(x => x.HashIndex).Returns(DuplicatesSelectionTestHelpers.BuildEmptyHashIndex());

        var selectionContext = new DuplicateExplorerSelectionContext();

        var vm = new TreeMapController(host.Object, selectionContext, new DisposableManager())
        {
            Options = new TreeMapBuildOptions
            {
                MaxDepth = 8,
                MaxSubdirsPerDir = 64,
                MaxFilesPerDir = 64,
                MaxTotalFileNodes = 1024
            }
        };

        vm.Rebuild(snapshot);

        var file700 = new FileHandle(1, 0);
        vm.SelectedNode = vm.FileNodeByHandle[file700];

        var current = Assert.IsType<DuplicateExplorerSelectionContext.SelectionTarget>(selectionContext.Current);
        Assert.Equal(DuplicateExplorerSelectionContext.SelectionKind.File, current.Kind);
        Assert.Equal(700, current.FileId);
        Assert.Equal(200, current.ParentDirId);
    }
}
