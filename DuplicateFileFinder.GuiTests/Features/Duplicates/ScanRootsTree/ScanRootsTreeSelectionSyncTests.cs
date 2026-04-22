using DuplicateFileFinder.Gui.Application.Deletion;
using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;
using DuplicateFileFinder.GuiTests.Features.Duplicates.TestSupport;
using DuplicateFileFinder.GuiTests.UI.Fakes;

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

        var repo = new FakeRepo(snapshot.ScanRoots.Values)
        {
            SnapshotToReturn = snapshot
        };

        var fileDir = new FakeFileDirReadModel();
        DuplicatesSelectionTestHelpers.ResetAndSeedFileDir(fileDir, snapshot);

        var treeIndex = new FakeTreeIndex();
        DuplicatesSelectionTestHelpers.ConfigureTreeIndex(treeIndex, snapshot);

        var host = new FakeRepoHost(repo)
        {
            FileDirIndex = fileDir,
            TreeIndex = treeIndex,
            HashIndex = new FakeHashIndex()
        };

        var selectionContext = new DuplicateExplorerSelectionContext();

        var vm = new ScanRootsTreeViewModel(
            host,
            new RepoUiEventRelayPlugin(new AvaloniaUiDispatcher()),
            new ScanRootsTreeBuilder(host),
            Mock.Of<IScanRootsTreeNodeActions>(),
            Mock.Of<IDeletionWorkflowService>(),
            new DisposableManager(),
            selectionContext);

        vm.Rebuild(snapshot);

        Assert.True(DuplicatesSelectionTestHelpers.TryGetDirHandle(snapshot, 1, 300, out var dir300));

        vm.NavigateToDir(dir300);

        var current = Assert.IsType<DuplicateExplorerSelectionContext.SelectionTarget>(selectionContext.Current);
        Assert.Equal(DuplicateExplorerSelectionContext.SelectionKind.Directory, current.Kind);
        Assert.Equal(300, current.ContextDirectoryId);
        Assert.Equal(200, current.ParentOfContextDirectoryId);

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

        var repo = new FakeRepo(snapshot.ScanRoots.Values)
        {
            SnapshotToReturn = snapshot
        };

        var fileDir = new FakeFileDirReadModel();
        DuplicatesSelectionTestHelpers.ResetAndSeedFileDir(fileDir, snapshot);

        var treeIndex = new FakeTreeIndex();
        DuplicatesSelectionTestHelpers.ConfigureTreeIndex(treeIndex, snapshot);

        var host = new FakeRepoHost(repo)
        {
            FileDirIndex = fileDir,
            TreeIndex = treeIndex,
            HashIndex = new FakeHashIndex()
        };

        var selectionContext = new DuplicateExplorerSelectionContext();

        var vm = new ScanRootsTreeViewModel(
            host,
            new RepoUiEventRelayPlugin(new AvaloniaUiDispatcher()),
            new ScanRootsTreeBuilder(host),
            Mock.Of<IScanRootsTreeNodeActions>(),
            Mock.Of<IDeletionWorkflowService>(),
            new DisposableManager(),
            selectionContext);

        vm.Rebuild(snapshot);

        Assert.True(DuplicatesSelectionTestHelpers.TryGetDirHandle(snapshot, 1, 200, out var dir200));

        vm.NavigateToDir(dir200);
        selectionContext.Current = DuplicateExplorerSelectionContext.SelectionTarget.ForDirectory([100, 200]);

        Assert.NotNull(vm.SelectedRow);
        Assert.Equal(200, snapshot.GetDirRecord(vm.SelectedRow!.Dir).DirId);
    }
}
