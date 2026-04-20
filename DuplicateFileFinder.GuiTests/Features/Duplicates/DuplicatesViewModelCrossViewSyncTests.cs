using DuplicateFileFinder.Gui.Application.Deletion;
using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;
using DuplicateFileFinder.GuiTests.Features.Duplicates.TestSupport;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;

using Moq;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates;

public sealed class DuplicatesViewModelCrossViewSyncTests
{
    [Fact]
    public void TreeNavigation_UpdatesTreeMapSelection_AndDuplicateGroupsFilter()
    {
        var snapshot = DuplicatesSelectionTestHelpers.BuildSnapshot(
            scanRootId: 1,
            dirs:
            [
                new(100, -1, "root"),
                new(200, 100, "A"),
                new(300, 200, "B")
            ],
            files:
            [
                new(700, 300, "f.txt", 123)
            ]);

        var env = CreateSut(snapshot);

        Assert.True(DuplicatesSelectionTestHelpers.TryGetDirHandle(snapshot, 1, 300, out var dir300));

        env.ViewModel.ScanRootsTree.NavigateToDir(dir300);

        var current = Assert.IsType<DuplicateExplorerSelectionContext.SelectionTarget>(env.SelectionContext.Current);
        Assert.Equal(DuplicateExplorerSelectionContext.SelectionKind.Directory, current.Kind);
        Assert.Equal(300, current.DirId);

        Assert.NotNull(env.ViewModel.TreeMapController.SelectedNode);
        var selectedDir = Assert.IsType<DirTreeMapElement>(env.ViewModel.TreeMapController.SelectedNode!.Element);
        Assert.Equal(300, snapshot.GetDirRecord(selectedDir.Dir).DirId);

        Assert.Equal(dir300, env.DuplicateGroups.SelectedSubtreeDir);
    }

    [Fact]
    public void TreeMapFileSelection_UpdatesTreeSelection_AndDuplicateGroupsFilterToParentDirectory()
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

        var env = CreateSut(snapshot);

        var file700 = new FileHandle(1, 0);
        env.ViewModel.TreeMapController.SelectedNode = env.ViewModel.TreeMapController.FileNodeByHandle[file700];


        var current = Assert.IsType<DuplicateExplorerSelectionContext.SelectionTarget>(env.SelectionContext.Current);
        Assert.Equal(DuplicateExplorerSelectionContext.SelectionKind.File, current.Kind);
        Assert.Equal(700, current.FileId);
        Assert.Equal(200, current.ParentDirId);

        Assert.NotNull(env.ViewModel.ScanRootsTree.SelectedRow);
        Assert.Equal(200, snapshot.GetDirRecord(env.ViewModel.ScanRootsTree.SelectedRow!.Dir).DirId);

        Assert.Equal(new DirHandle(1, 1), env.DuplicateGroups.SelectedSubtreeDir);
    }

    private static Sut CreateSut(RepoSnapshotView snapshot)
    {
        var repo = new FakeRepo(snapshot.ScanRoots.Values) { SnapshotToReturn = snapshot };
        var fileDir = new FakeFileDirReadModel();
        DuplicatesSelectionTestHelpers.SeedFileDir(fileDir, snapshot);

        var treeIndex = DuplicatesSelectionTestHelpers.BuildTreeIndex(snapshot);
        var hashIndex = DuplicatesSelectionTestHelpers.BuildEmptyHashIndex();

        var host = new Mock<IRepoHost>(MockBehavior.Strict);
        host.SetupGet(x => x.Repo).Returns(repo);
        host.SetupGet(x => x.FileDirIndex).Returns(fileDir);
        host.SetupGet(x => x.TreeIndex).Returns(treeIndex);
        host.SetupGet(x => x.HashIndex).Returns(hashIndex);
        host.SetupGet(x => x.LastIndexedGeneration).Returns(1);

        var selectionContext = new DuplicateExplorerSelectionContext();

        var treeVm = new ScanRootsTreeViewModel(
            new RepoUiEventRelayPlugin(new AvaloniaUiDispatcher()),
            new ScanRootsTreeBuilder(host.Object),
            Mock.Of<IScanRootsTreeNodeActions>(),
            Mock.Of<IDeletionWorkflowService>(),
            new DisposableManager(),
            selectionContext);

        var treeMapVm = new TreeMapController(host.Object, selectionContext, new DisposableManager())
        {
            Metric = TreeMapMetric.TotalBytes,
            Options = new TreeMapBuildOptions
            {
                MaxDepth = 8,
                MaxSubdirsPerDir = 64,
                MaxFilesPerDir = 64,
                MaxTotalFileNodes = 1024
            }
        };

        var duplicateGroupsController = new DuplicateGroupsController(host.Object);
        var duplicateGroups = new DuplicateGroupsViewModel(
            host.Object,
            duplicateGroupsController,
            Mock.Of<IDeletionWorkflowService>(),
            Mock.Of<IClipboardService>());

        var treeMapActions = new TreeMapActionsViewModel(
            host.Object,
            Mock.Of<IScanCoordinator>(),
            Mock.Of<IDeletionWorkflowService>(),
            Mock.Of<IClipboardService>(),
            new DisposableManager());

        var vm = new DuplicatesViewModel(
            host.Object,
            treeVm,
            treeMapVm,
            treeMapActions,
            duplicateGroups,
            selectionContext,
            new DisposableManager());

        return new Sut(vm, selectionContext, duplicateGroups);
    }

    private sealed record Sut(
        DuplicatesViewModel ViewModel,
        DuplicateExplorerSelectionContext SelectionContext,
        DuplicateGroupsViewModel DuplicateGroups);
}
