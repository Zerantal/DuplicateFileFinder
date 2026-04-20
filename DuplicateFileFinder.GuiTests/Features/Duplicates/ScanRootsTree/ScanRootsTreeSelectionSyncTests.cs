using DuplicateFileFinder.Gui.Application.Deletion;
using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;
using DuplicateFileFinder.GuiTests.Features.Duplicates.TestSupport;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Interfaces;

using Moq;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates.ScanRootsTree;

public sealed class ScanRootsTreeSelectionSyncTests
{
    [Fact]
    public void NavigateToDir_PublishesDirectorySelectionToSharedContext()
    {
        var snapshot = DuplicatesSelectionTestHelpers.BuildSnapshot(
            scanRootId: 1,
            dirs:
            [
                new(100, -1, "root"),
                new(200, 100, "A"),
                new(300, 200, "B")
            ],
            files: []);

        var repo = new FakeRepo(snapshot.ScanRoots.Values) { SnapshotToReturn = snapshot };
        var fileDir = new FakeFileDirReadModel();
        DuplicatesSelectionTestHelpers.SeedFileDir(fileDir, snapshot);

        var treeIndex = DuplicatesSelectionTestHelpers.BuildTreeIndex(snapshot);

        var host = new Mock<IRepoHost>(MockBehavior.Strict);
        host.SetupGet(x => x.Repo).Returns(repo);
        host.SetupGet(x => x.FileDirIndex).Returns(fileDir);
        host.SetupGet(x => x.TreeIndex).Returns(treeIndex);
        host.SetupGet(x => x.HashIndex).Returns(DuplicatesSelectionTestHelpers.BuildEmptyHashIndex());
        host.SetupGet(x => x.LastIndexedGeneration).Returns(1);

        var selectionContext = new DuplicateExplorerSelectionContext();

        var vm = new ScanRootsTreeViewModel(
            new RepoUiEventRelayPlugin(new AvaloniaUiDispatcher()),
            new ScanRootsTreeBuilder(host.Object),
            Mock.Of<IScanRootsTreeNodeActions>(),
            Mock.Of<IDeletionWorkflowService>(),
            new DisposableManager(),
            selectionContext);

        vm.Rebuild(snapshot);

        Assert.True(DuplicatesSelectionTestHelpers.TryGetDirHandle(snapshot, 1, 300, out var dir300));

        vm.NavigateToDir(dir300);

        var current = Assert.IsType<DuplicateExplorerSelectionContext.SelectionTarget>(selectionContext.Current);
        Assert.Equal(DuplicateExplorerSelectionContext.SelectionKind.Directory, current.Kind);
        Assert.Equal(300, current.DirId);
        Assert.Equal(200, current.ParentDirId);

        Assert.NotNull(vm.SelectedRow);
        Assert.Equal(300, snapshot.GetDirRecord(vm.SelectedRow!.Dir).DirId);
    }

    [Fact]
    public void SettingSharedDirectorySelection_SelectsMatchingVisibleTreeRow()
    {
        var snapshot = DuplicatesSelectionTestHelpers.BuildSnapshot(
            scanRootId: 1,
            dirs:
            [
                new(100, -1, "root"),
                new(200, 100, "A")
            ],
            files: []);

        var repo = new FakeRepo(snapshot.ScanRoots.Values) { SnapshotToReturn = snapshot };
        var fileDir = new FakeFileDirReadModel();
        DuplicatesSelectionTestHelpers.SeedFileDir(fileDir, snapshot);

        var treeIndex = DuplicatesSelectionTestHelpers.BuildTreeIndex(snapshot);

        var host = new Mock<IRepoHost>(MockBehavior.Strict);
        host.SetupGet(x => x.Repo).Returns(repo);
        host.SetupGet(x => x.FileDirIndex).Returns(fileDir);
        host.SetupGet(x => x.TreeIndex).Returns(treeIndex);
        host.SetupGet(x => x.HashIndex).Returns(DuplicatesSelectionTestHelpers.BuildEmptyHashIndex());
        host.SetupGet(x => x.LastIndexedGeneration).Returns(1);

        var selectionContext = new DuplicateExplorerSelectionContext();

        var vm = new ScanRootsTreeViewModel(
            new RepoUiEventRelayPlugin(new AvaloniaUiDispatcher()),
            new ScanRootsTreeBuilder(host.Object),
            Mock.Of<IScanRootsTreeNodeActions>(),
            Mock.Of<IDeletionWorkflowService>(),
            new DisposableManager(),
            selectionContext);

        vm.Rebuild(snapshot);

        Assert.True(DuplicatesSelectionTestHelpers.TryGetDirHandle(snapshot, 1, 200, out var dir200));

        vm.NavigateToDir(dir200);
        selectionContext.Current = DuplicateExplorerSelectionContext.SelectionTarget.ForDirectory(200, 100);

        Assert.NotNull(vm.SelectedRow);
        Assert.Equal(200, snapshot.GetDirRecord(vm.SelectedRow!.Dir).DirId);
    }
}
