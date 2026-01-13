// DuplicateFileFinder.GuiTests/Features/Controller/ScanRootsTree/FolderNodeViewModelTests.cs

using System.Collections.Generic;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates.ScanRootsTree;

public sealed class FolderNodeViewModelTests
{
    [Fact]
    public void DisplayName_UsesFullPathWhenShowFullPath_AndAppendsStatusTag()
    {
        var actions = new TestActions();

        var model = NewModel(name: "Foo", fullPath: "/a/b/Foo", scanRootId: 1, isScanRoot: false);
        var node = new FolderNodeViewModel(model, actions) { Parent = NewRootVm(actions, scanRootId: 1) };

        Assert.Equal("Foo", node.DisplayName);

        model.StatusTag = "[deleted]";
        Assert.Equal("Foo [deleted]", node.DisplayName);

        node.ShowFullPath = true;
        Assert.Equal("/a/b/Foo [deleted]", node.DisplayName);

        model.StatusTag = null;
        Assert.Equal("/a/b/Foo", node.DisplayName);
    }

    [Fact]
    public void AggregateStats_AreProjectedFromModel_AndPercentIsComputedByModel()
    {
        var actions = new TestActions();

        var model = NewModel(name: "N", fullPath: "/n", scanRootId: 1, isScanRoot: false);
        model.ApplyAggregateStats(
            new DirAggregateStats
            {
                TotalBytes = 50,
                FileCount = 3,
                DirCount = 2,
                DuplicateFiles = 1,
                DuplicateBytes = 10
            },
            scanRootTotalBytes: 200);

        var node = new FolderNodeViewModel(model, actions) { Parent = NewRootVm(actions, scanRootId: 1) };

        Assert.Equal(50, node.TotalBytes);
        Assert.Equal(3, node.FileCount);
        Assert.Equal(2, node.DirCount);
        Assert.Equal(5, node.ItemCount);
        Assert.Equal(1, node.DuplicateFiles);
        Assert.Equal(10, node.DuplicateBytes);
        Assert.Equal(200, node.ScanRootTotalBytes);
        Assert.Equal(25.0, node.PercentOfScanRoot, 6);
    }

    [Fact]
    public void AggregateStats_WhenScanRootTotalBytesNonPositive_SetsPercentZero()
    {
        var actions = new TestActions();

        var model = NewModel(name: "N", fullPath: "/n", scanRootId: 1, isScanRoot: false);
        model.ApplyAggregateStats(
            new DirAggregateStats
            {
                TotalBytes = 50,
                FileCount = 0,
                DirCount = 0,
                DuplicateFiles = 0,
                DuplicateBytes = 0
            },
            scanRootTotalBytes: 0);

        var node = new FolderNodeViewModel(model, actions) { Parent = NewRootVm(actions, scanRootId: 1) };

        Assert.Equal(0, node.ScanRootTotalBytes);
        Assert.Equal(0.0, node.PercentOfScanRoot);
    }

    [Fact]
    public void IsExpanded_InvokesEnsureChildrenLoaded_WhenSetTrue()
    {
        var actions = new TestActions();

        FolderNodeViewModel? calledWith = null;

        var model = NewModel(name: "N", fullPath: "/n", scanRootId: 1, isScanRoot: false);
        var node = new FolderNodeViewModel(model, actions)
        {
            Parent = NewRootVm(actions, scanRootId: 1),
            EnsureChildrenLoaded = n => calledWith = n
        };

        Assert.False(node.IsExpanded);

        node.IsExpanded = true;

        Assert.True(node.IsExpanded);
        Assert.Same(node, calledWith);
    }

    [Fact]
    public void DummyChildHelpers_AddDummyChildAndDetectsIt()
    {
        var actions = new TestActions();

        var model = NewModel(name: "N", fullPath: "/n", scanRootId: 1, isScanRoot: false);
        var node = new FolderNodeViewModel(model, actions) { Parent = NewRootVm(actions, scanRootId: 1) };

        Assert.False(node.HasDummyChild);
        Assert.Empty(node.Children);

        node.AddDummyChild();

        Assert.True(node.HasDummyChild);
        Assert.Single(node.Children);

        node.ClearChildren();
        Assert.Empty(node.Children);
        Assert.False(node.HasDummyChild);
    }

    [Fact]
    public async Task RescanFolder_WhenDirInvalid_DoesNothing()
    {
        var actions = new TestActions();

        var model = NewModel(name: "N", fullPath: "/n", scanRootId: 1, isScanRoot: false, dir: DirHandle.Invalid);
        var node = new FolderNodeViewModel(model, actions)
        {
            Parent = NewRootVm(actions, scanRootId: 1)
        };

        await node.RescanFolderCommand.ExecuteAsync(null);

        Assert.Empty(actions.RescannedFolders);
    }

    [Fact]
    public async Task RescanFolder_WhenValid_InvokesActions()
    {
        var actions = new TestActions();

        var dir = new DirHandle(1, 123);
        var model = NewModel(name: "N", fullPath: "/n", scanRootId: 1, isScanRoot: false, dir: dir);
        var node = new FolderNodeViewModel(model, actions)
        {
            Parent = NewRootVm(actions, scanRootId: 1)
        };

        await node.RescanFolderCommand.ExecuteAsync(null);

        var called = Assert.Single(actions.RescannedFolders);
        Assert.Equal(dir, called);
    }

    [Fact]
    public async Task RescanLocation_WhenNotScanRoot_DoesNothing()
    {
        var actions = new TestActions();

        var childModel = NewModel(name: "Child", fullPath: "/root/child", scanRootId: 7, isScanRoot: false);
        var child = new FolderNodeViewModel(childModel, actions)
        {
            Parent = NewRootVm(actions, scanRootId: 7)
        };

        await child.RescanLocationCommand.ExecuteAsync(null);

        Assert.Empty(actions.RescannedScanRoots);
    }

    [Fact]
    public async Task RescanLocation_WhenScanRoot_InvokesActions()
    {
        var actions = new TestActions();

        var root = NewRootVm(actions, scanRootId: 7);

        await root.RescanLocationCommand.ExecuteAsync(null);

        Assert.Single(actions.RescannedScanRoots);
        Assert.Equal(7L, actions.RescannedScanRoots[0]);
    }

    [Fact]
    public async Task RemoveLocation_WhenNotScanRoot_DoesNothing()
    {
        var actions = new TestActions { NextRemoveResult = true };

        var childModel = NewModel(name: "Child", fullPath: "/root/child", scanRootId: 7, isScanRoot: false);
        var child = new FolderNodeViewModel(childModel, actions)
        {
            Parent = NewRootVm(actions, scanRootId: 7)
        };

        var removedCalled = false;
        child.OnRootRemoved = _ => removedCalled = true;

        await child.RemoveLocationCommand.ExecuteAsync(null);

        Assert.False(removedCalled);
        Assert.Empty(actions.RemovedScanRoots);
    }

    [Fact]
    public async Task RemoveLocation_WhenScanRoot_AndActionsReturnsFalse_DoesNotInvokeOnRootRemoved()
    {
        var actions = new TestActions { NextRemoveResult = false };

        var root = NewRootVm(actions, scanRootId: 7);

        var removedCalled = false;
        root.OnRootRemoved = _ => removedCalled = true;

        await root.RemoveLocationCommand.ExecuteAsync(null);

        Assert.False(removedCalled);
        Assert.Single(actions.RemovedScanRoots);
        Assert.Equal(7L, actions.RemovedScanRoots[0]);
    }

    [Fact]
    public async Task RemoveLocation_WhenScanRoot_AndActionsReturnsTrue_InvokesOnRootRemoved()
    {
        var actions = new TestActions { NextRemoveResult = true };

        var root = NewRootVm(actions, scanRootId: 7);

        var removedCalled = false;
        root.OnRootRemoved = _ => removedCalled = true;

        await root.RemoveLocationCommand.ExecuteAsync(null);

        Assert.True(removedCalled);
        Assert.Single(actions.RemovedScanRoots);
        Assert.Equal(7L, actions.RemovedScanRoots[0]);
    }

    [Fact]
    public async Task SetDisplayName_WhenCancelled_DoesNotRequestRefresh()
    {
        var actions = new TestActions { NextSetDisplayNameResult = false };

        var root = NewRootVm(actions, scanRootId: 7);

        var refreshCalled = false;
        root.OnRootLabelRefreshRequested = () => refreshCalled = true;

        await root.SetDisplayNameCommand.ExecuteAsync(null);

        Assert.False(refreshCalled);
        Assert.Single(actions.SetDisplayNameCalls);
        Assert.Equal((7L, "Root"), actions.SetDisplayNameCalls[0]);
    }

    [Fact]
    public async Task SetDisplayName_WhenApplied_RequestsRefresh()
    {
        var actions = new TestActions { NextSetDisplayNameResult = true };

        var root = NewRootVm(actions, scanRootId: 7);

        var refreshCalled = false;
        root.OnRootLabelRefreshRequested = () => refreshCalled = true;

        await root.SetDisplayNameCommand.ExecuteAsync(null);

        Assert.True(refreshCalled);
        Assert.Single(actions.SetDisplayNameCalls);
        Assert.Equal((7L, "Root"), actions.SetDisplayNameCalls[0]);
    }

    [Fact]
    public void CanDeleteFromDisk_IsFalse_ForScanRoot_AndDummy()
    {
        var actions = new TestActions();

        var scanRoot = NewRootVm(actions, scanRootId: 1);

        Assert.True(scanRoot.IsScanRoot);
        Assert.False(scanRoot.CanDeleteFromDisk);

        // emulate dummy by passing isDummy=true and giving it a parent
        var dummyModel = NewModel(name: "Loading...", fullPath: "", scanRootId: 1, isScanRoot: false, dir: DirHandle.Invalid);
        var dummy = new FolderNodeViewModel(dummyModel, actions, isDummy: true)
        {
            Parent = scanRoot
        };

        Assert.False(dummy.IsScanRoot);
        Assert.False(dummy.CanDeleteFromDisk);
    }

    [Fact]
    public async Task DeleteFromDisk_WhenNotAllowed_DoesNothing()
    {
        var actions = new TestActions();

        var root = NewRootVm(actions, scanRootId: 1);
        await root.DeleteFromDiskCommand.ExecuteAsync(null);

        Assert.Empty(actions.DeletedFolders);
    }

    [Fact]
    public async Task DeleteFromDisk_WhenAllowed_InvokesActions()
    {
        var actions = new TestActions();

        var dir = new DirHandle(1, 10);
        var childModel = NewModel(name: "Child", fullPath: "/root/child", scanRootId: 1, isScanRoot: false, dir: dir);
        var child = new FolderNodeViewModel(childModel, actions)
        {
            Parent = NewRootVm(actions, scanRootId: 1)
        };

        await child.DeleteFromDiskCommand.ExecuteAsync(null);

        var deleted = Assert.Single(actions.DeletedFolders);
        Assert.Equal(dir, deleted.Dir);
        Assert.Equal("/root/child", deleted.FullPath);
    }

    // ---------------------------------------------------------------------
    // Helpers / fakes
    // ---------------------------------------------------------------------

    private static FolderNodeViewModel NewRootVm(TestActions actions, long scanRootId)
    {
        var rootModel = NewModel(name: "Root", fullPath: "/root", scanRootId: scanRootId, isScanRoot: true, dir: DirHandle.Invalid);
        // Root-ness is determined by Parent==null in the VM.
        return new FolderNodeViewModel(rootModel, actions);
    }

    private static ScanRootsTreeNode NewModel(
        string name,
        string fullPath,
        long scanRootId,
        bool isScanRoot,
        DirHandle? dir = null)
    {
        return new ScanRootsTreeNode
        {
            Dir = dir ?? new DirHandle(scanRootId, 1),
            Name = name,
            FullPath = fullPath,
            ScanRootId = scanRootId,
            IsScanRoot = isScanRoot,

            // reasonable defaults for tests
            IsAvailable = true,
            ChildrenMaterialized = true,
            HasLazyChildren = false
        };
    }

    private sealed class TestActions : IScanRootsTreeNodeActions
    {
        public readonly List<long> RescannedScanRoots = new();
        public readonly List<DirHandle> RescannedFolders = new();
        public readonly List<long> RemovedScanRoots = new();
        public readonly List<(long ScanRootId, string CurrentLabel)> SetDisplayNameCalls = new();
        public readonly List<(DirHandle Dir, string FullPath)> DeletedFolders = new();

        public bool NextRemoveResult { get; set; } = true;
        public bool NextSetDisplayNameResult { get; set; } = true;

        public Task RescanScanRootAsync(long scanRootId)
        {
            RescannedScanRoots.Add(scanRootId);
            return Task.CompletedTask;
        }

        public Task RescanFolderAsync(DirHandle dir)
        {
            RescannedFolders.Add(dir);
            return Task.CompletedTask;
        }

        public Task<bool> TryRemoveScanRootAsync(long scanRootId)
        {
            RemovedScanRoots.Add(scanRootId);
            return Task.FromResult(NextRemoveResult);
        }

        public Task<bool> TrySetScanRootDisplayNameAsync(long scanRootId, string currentLabel)
        {
            SetDisplayNameCalls.Add((scanRootId, currentLabel));
            return Task.FromResult(NextSetDisplayNameResult);
        }

        public Task DeleteFolderAsync(DirHandle dir, string fullPath)
        {
            DeletedFolders.Add((dir, fullPath));
            return Task.CompletedTask;
        }
    }
}
