using System.Collections.Generic;

using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Util;
using DuplicateFileFinder.GuiTests.Features.Duplicates.TestSupport;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Core.Models;

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

        var vm = new TreeMapController(host, selectionContext, new DisposableManager())
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

        vm.Rebuild(snapshot);

        Assert.True(DuplicatesSelectionTestHelpers.TryGetDirHandle(snapshot, 1, 200, out var dir200));
        vm.SelectedNode = vm.DirNodeByHandle[dir200];

        var current = Assert.IsType<DuplicateExplorerSelectionContext.SelectionTarget>(selectionContext.Current);
        Assert.Equal(DuplicateExplorerSelectionContext.SelectionKind.Directory, current.Kind);
        Assert.Equal(200, current.ContextDirectoryId);
        Assert.Equal(100, current.ParentOfContextDirectoryId);
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

        var vm = new TreeMapController(host, selectionContext, new DisposableManager())
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

        vm.Rebuild(snapshot);

        var file700 = new FileHandle(1, 0);
        var fileNode = FindFileNode(vm.Root, file700);

        Assert.NotNull(fileNode);
        vm.SelectedNode = fileNode;

        var current = Assert.IsType<DuplicateExplorerSelectionContext.SelectionTarget>(selectionContext.Current);
        Assert.Equal(DuplicateExplorerSelectionContext.SelectionKind.File, current.Kind);
        Assert.Equal(700, current.FileId);
        Assert.Equal(200, current.ContextDirectoryId);
    }

    private static TreeMapNode<ITreeMapNodeElement>? FindFileNode(
        TreeMapNode<ITreeMapNodeElement>? root,
        FileHandle file)
    {
        if (root is null)
            return null;

        foreach (var node in Traverse(root))
        {
            if (node.Element is FileTreeMapElement fileElement && fileElement.File == file)
                return node;
        }

        return null;
    }

    private static IEnumerable<TreeMapNode<ITreeMapNodeElement>> Traverse(TreeMapNode<ITreeMapNodeElement> root)
    {
        yield return root;

        foreach (var child in root.Children)
        {
            foreach (var descendant in Traverse(child))
                yield return descendant;
        }
    }
}
