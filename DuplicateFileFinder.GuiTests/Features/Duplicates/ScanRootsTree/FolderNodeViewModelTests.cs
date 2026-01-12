// DuplicateFileFinder.GuiTests/Features/Controller/ScanRootsTree/FolderNodeViewModelTests.cs

using System;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates.ScanRootsTree;

public sealed class FolderNodeViewModelTests
{
    [Fact]
    public void DisplayName_UsesFullPathWhenShowFullPath_AndAppendsStatusTag()
    {
        var node = TestObjectFactory.CreateFolderNode(
            "Foo",
            "/a/b/Foo",
            new FolderNodeViewModel(
                DirHandle.Invalid,
                "P", "/a/b",
                null,
                null,
                null,
                null,
                1));

        Assert.Equal("Foo", node.DisplayName);

        node.StatusTag = "[deleted]";
        Assert.Equal("Foo [deleted]", node.DisplayName);

        node.ShowFullPath = true;
        Assert.Equal("/a/b/Foo [deleted]", node.DisplayName);

        node.StatusTag = null;
        Assert.Equal("/a/b/Foo", node.DisplayName);
    }

    [Fact]
    public void ApplyAggregateStats_SetsFields_AndComputesPercent()
    {
        var node = TestObjectFactory.CreateFolderNode(
            "N",
            "/n",
            new FolderNodeViewModel(
                DirHandle.Invalid,
                "P",
                "/p",
                null,
                null,
                null,
                null, 1));

        node.ApplyAggregateStats(
            new DirAggregateStats
            {
                TotalBytes = 50,
                FileCount = 3,
                DirCount = 2,
                DuplicateFiles = 1,
                DuplicateBytes = 10
            },
            200);

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
    public void ApplyAggregateStats_WhenScanRootTotalBytesNonPositive_SetsPercentZero()
    {
        var node = TestObjectFactory.CreateFolderNode(
            "N",
            "/n",
            new FolderNodeViewModel(
                DirHandle.Invalid,
                "P",
                "/p",
                null,
                null,
                null,
                null,
                1)
            );

        node.ApplyAggregateStats(
            new DirAggregateStats
            {
                TotalBytes = 50,
                FileCount = 0,
                DirCount = 0,
                DuplicateFiles = 0,
                DuplicateBytes = 0
            },
            0);

        Assert.Equal(0, node.ScanRootTotalBytes);
        Assert.Equal(0.0, node.PercentOfScanRoot);
    }

    [Fact]
    public void IsExpanded_InvokesEnsureChildrenLoaded_WhenSetTrue()
    {
        FolderNodeViewModel? calledWith = null;
        var node = TestObjectFactory.CreateFolderNode(
            "N",
            "/n",
            new FolderNodeViewModel(
                DirHandle.Invalid,
                "P",
                "/p",
                null,
                null,
                null,
                null,
                1),
            ensureChildrenLoaded: n => calledWith = n
        );

        Assert.False(node.IsExpanded);

        node.IsExpanded = true;

        Assert.True(node.IsExpanded);
        Assert.Same(node, calledWith);
    }

    [Fact]
    public void DummyChildHelpers_AddDummyChildAndDetectsIt()
    {
        var node = TestObjectFactory.CreateFolderNode(
            "N",
            "/n",
            new FolderNodeViewModel(
                DirHandle.Invalid,
                "P",
                "/p",
                null,
                null,
                null,
                null,
                1)
            );

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
        var scanner = new FakeScanCoordinator();
        var node = new FolderNodeViewModel(
            DirHandle.Invalid,
            "N",
            "/n",
            scanner,
            null,
            null,
            null,
            1)
        {
            // Non-root: set parent
            Parent = new FolderNodeViewModel(DirHandle.Invalid, "P", "/p", scanner, null, null, null, 1)
        };

        await node.RescanFolderCommand.ExecuteAsync(null);

        Assert.Empty(scanner.RescannedFolders);
    }

    [Fact]
    public async Task RescanFolder_WhenValid_InvokesScanner()
    {
        var scanner = new FakeScanCoordinator();
        var node = TestObjectFactory.CreateFolderNode(
            "N",
            "/n",
            new FolderNodeViewModel(
                DirHandle.Invalid,
                "P",
                "/p",
                scanner,
                null,
                null,
                null,
                1),
            scanner,
            dir: new DirHandle(1, 123));

        await node.RescanFolderCommand.ExecuteAsync(null);

        Assert.Single(scanner.RescannedFolders);
        Assert.Equal(new DirHandle(1, 123), scanner.RescannedFolders[0]);
    }

    [Fact]
    public async Task RemoveLocation_WhenNotScanRoot_DoesNothing()
    {
        var scanner = new FakeScanCoordinator();
        var dialogs = new FakeDialogService();
        var node = TestObjectFactory.CreateFolderNode(
            "Child",
            "/root/child",
            new FolderNodeViewModel(DirHandle.Invalid,
                "Root",
                "/root",
                scanner,
                dialogs,
                null,
                null,
                1),
            scanner,
            dialogs);

        var removedCalled = false;
        node.OnRootRemoved = _ => removedCalled = true;

        await node.RemoveLocationCommand.ExecuteAsync(null);

        Assert.False(removedCalled);
        Assert.Empty(dialogs.Confirmations);
    }

    [Fact]
    public async Task RemoveLocation_WhenUserCancels_DoesNothing()
    {
        var scanner = new FakeScanCoordinator();
        var dialogs = new FakeDialogService { NextConfirmResult = false };

        var root = new FolderNodeViewModel(
            DirHandle.Invalid,
            "Root",
            "/root",
            scanner,
            dialogs,
            null,
            null,
            7);

        var removedCalled = false;
        root.OnRootRemoved = _ => removedCalled = true;

        await root.RemoveLocationCommand.ExecuteAsync(null);

        Assert.False(removedCalled);
        Assert.Single(dialogs.Confirmations);
    }

    [Fact]
    public async Task SetDisplayName_WhenCancelled_DoesNotUpdateDisplayName_AndDoesNotRefresh()
    {
        var scanner = new FakeScanCoordinator();
        var dialogs = new FakeDialogService() { NextTextInput = null };

        var root = new FolderNodeViewModel(
            dir: DirHandle.Invalid,
            name: "Root",
            fullPath: "/root",
            scanCoordinator: scanner,
            dialogs: dialogs,
            deleter: null,
            repo: null,
            scanRootId: 7);

        var refreshCalled = false;
        root.OnRootLabelRefreshRequested = () => refreshCalled = true;

        await root.SetDisplayNameCommand.ExecuteAsync(null);

        // Dialog should be shown
        Assert.Single(dialogs.TextInputs);

        // But no update should be applied
        Assert.Empty(scanner.DisplayNameUpdates);

        // And no UI refresh should be triggered
        Assert.False(refreshCalled);
    }

    [Fact]
    public async Task SetDisplayName_WhenBlank_SetsNull_AndRequestsRefresh()
    {
        var scanner = new FakeScanCoordinator();
        var dialogs = new FakeDialogService() { NextTextInput = "   " };

        var root = new FolderNodeViewModel(
            dir: DirHandle.Invalid,
            name: "Root",
            fullPath: "/root",
            scanCoordinator: scanner,
            dialogs: dialogs,
            deleter: null,
            repo: null,
            scanRootId: 7);

        var refreshCalled = false;
        root.OnRootLabelRefreshRequested = () => refreshCalled = true;

        await root.SetDisplayNameCommand.ExecuteAsync(null);

        Assert.True(refreshCalled);
        Assert.Single(scanner.DisplayNameUpdates);
        Assert.Equal((7L, (string?)null), scanner.DisplayNameUpdates[0]);
    }

    [Fact]
    public async Task SetDisplayName_WhenNonBlank_SetsValue_AndRequestsRefresh()
    {
        var scanner = new FakeScanCoordinator();
        var dialogs = new FakeDialogService() { NextTextInput = "My Root" };

        var root = new FolderNodeViewModel(
            dir: DirHandle.Invalid,
            name: "Root",
            fullPath: "/root",
            scanCoordinator: scanner,
            dialogs: dialogs,
            deleter: null,
            repo: null,
            scanRootId: 7);

        var refreshCalled = false;
        root.OnRootLabelRefreshRequested = () => refreshCalled = true;

        await root.SetDisplayNameCommand.ExecuteAsync(null);

        Assert.True(refreshCalled);
        Assert.Single(scanner.DisplayNameUpdates);
        Assert.Equal((7L, "My Root"), scanner.DisplayNameUpdates[0]);
    }

    [Fact]
    public void CanDeleteFromDisk_IsFalse_ForScanRoot_AndDummy()
    {
        var scanRoot = new FolderNodeViewModel(
            DirHandle.Invalid,
            "Root",
            "/root",
            new FakeScanCoordinator(),
            new FakeDialogService(),
            new FakeFileSystemDeleteService(),
            new FakeRepo([TestObjectFactory.NewScanRoot(1, "/root")]),
            1);

        Assert.True(scanRoot.IsScanRoot);
        Assert.False(scanRoot.CanDeleteFromDisk);

        // dummy instance is internal; we can emulate by passing isDummy=true
        var dummy = new FolderNodeViewModel(
            DirHandle.Invalid,
            "Loading...",
            "",
            new FakeScanCoordinator(),
            new FakeDialogService(),
            new FakeFileSystemDeleteService(),
            new FakeRepo([TestObjectFactory.NewScanRoot(1, "/root")]),
            1,
            true)
        { Parent = scanRoot };

        Assert.False(dummy.IsScanRoot);
        Assert.False(dummy.CanDeleteFromDisk);
    }

    [Fact]
    public async Task DeleteFromDisk_WhenUserCancels_DoesNothing()
    {
        var repo = new FakeRepo([TestObjectFactory.NewScanRoot(1, "/root")]);
        var dialogs = new FakeDialogService { NextConfirmResult = false };
        var deleter = new FakeFileSystemDeleteService();
        var scanner = new FakeScanCoordinator();

        var child = new FolderNodeViewModel(
            new DirHandle(1, 10),
            "Child",
            "/root/child",
            scanner,
            dialogs,
            deleter,
            repo,
            1)
        {
            Parent = new FolderNodeViewModel(
                DirHandle.Invalid,
                "Root",
                "/root",
                scanner,
                dialogs,
                deleter,
                repo,
                1)
        };

        await child.DeleteFromDiskCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Confirmations);
        Assert.Empty(deleter.DeletedDirectories);
        Assert.Empty(repo.DeletedDirs);
    }

    [Fact]
    public async Task DeleteFromDisk_WhenDeleteFails_ShowsError_AndDoesNotCallRepo()
    {
        var repo = new FakeRepo([TestObjectFactory.NewScanRoot(1, "/root")]);
        var dialogs = new FakeDialogService { NextConfirmResult = true };
        var deleter = new FakeFileSystemDeleteService { NextDeleteDirectoryResult = (false, "nope") };
        var scanner = new FakeScanCoordinator();

        var child = new FolderNodeViewModel(
            new DirHandle(1, 10),
            "Child",
            "/root/child",
            scanner,
            dialogs,
            deleter,
            repo,
            1)
        {
            Parent = new FolderNodeViewModel(
                DirHandle.Invalid,
                "Root",
                "/root",
                scanner,
                dialogs,
                deleter,
                repo,
                1)
        };

        await child.DeleteFromDiskCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Confirmations);
        Assert.Single(deleter.DeletedDirectories);
        Assert.Empty(repo.DeletedDirs);

        Assert.Contains(dialogs.Errors,
            e => e.Title == "Delete failed" && e.Message.Contains("nope", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteFromDisk_Success_DeletesDirectory_ThenCallsRepoDeleteDir()
    {
        var repo = new FakeRepo([TestObjectFactory.NewScanRoot(1, "/root")]);
        var dialogs = new FakeDialogService { NextConfirmResult = true };
        var deleter = new FakeFileSystemDeleteService { NextDeleteDirectoryResult = (true, null) };
        var scanner = new FakeScanCoordinator();

        var child = new FolderNodeViewModel(
            new DirHandle(1, 10),
            "Child",
            "/root/child",
            scanner,
            dialogs,
            deleter,
            repo,
            1)
        {
            Parent = new FolderNodeViewModel(
                DirHandle.Invalid,
                "Root",
                "/root",
                scanner,
                dialogs,
                deleter,
                repo,
                1)
        };

        await child.DeleteFromDiskCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Confirmations);
        Assert.Single(deleter.DeletedDirectories);
        Assert.Equal(("/root/child", true),
            (deleter.DeletedDirectories[0].Path, deleter.DeletedDirectories[0].Recursive));

        Assert.Single(repo.DeletedDirs);
        Assert.Equal(new DirHandle(1, 10), repo.DeletedDirs[0]);
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task DeleteFromDisk_WhenRepoDeleteFails_ShowsError()
    {
        var repo = new FakeRepo([TestObjectFactory.NewScanRoot(1, "/root")]);
        var dialogs = new FakeDialogService { NextConfirmResult = true };
        var deleter = new FakeFileSystemDeleteService { NextDeleteDirectoryResult = (true, null) };
        var scanner = new FakeScanCoordinator();

        var child = new FolderNodeViewModel(
            new DirHandle(1, 10),
            "Child",
            "/root/child",
            scanner,
            dialogs,
            deleter,
            repo,
            1)
        {
            Parent = new FolderNodeViewModel(
                DirHandle.Invalid,
                "Root",
                "/root",
                scanner,
                dialogs,
                deleter,
                repo,
                1)
        };

        repo.ReturnErrorOn.Add("DeleteDirAsync");

        await child.DeleteFromDiskCommand.ExecuteAsync(null);

        Assert.Single(repo.DeletedDirs);
        Assert.Contains(dialogs.Errors,
            e => e.Title == "Delete error" && e.Message.Contains("repository", StringComparison.OrdinalIgnoreCase));
    }
}
