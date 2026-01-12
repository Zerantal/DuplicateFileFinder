// DuplicateFileFinder.GuiTests/Features/Controller/TreeMap/TreeMapActionsViewModelTests.cs
using System;
using System.Linq;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates.TreeMap;

public sealed class TreeMapActionsViewModelTests
{
    [Fact]
    public void ContextTarget_UpdatesComputedProps_AndCanExecute()
    {
        var repo = new FakeRepo([
            NewScanRoot(rootId: 1, rootPath: "/root", volumePath: null, isDeleted: false)
        ]);

        var host = new FakeRepoHost(repo)
        {
            FileDirIndex = new FakeFileDirReadModel()
        };

        var scanner = new FakeScanCoordinator();
        var dialogs = new FakeDialogService();
        var deleter = new FakeFileSystemDeleteService();

        var vm = new TreeMapActionsViewModel(host, scanner, dialogs, deleter);

        Assert.False(vm.HasContextTarget);
        Assert.False(vm.IsContextDir);
        Assert.False(vm.IsContextFile);
        Assert.False(vm.RescanSelectedFolderCommand.CanExecute(null));
        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));

        vm.ContextTarget = NewDirElem(scanRootId: 1, dirIndex: 10);

        Assert.True(vm.HasContextTarget);
        Assert.True(vm.IsContextDir);
        Assert.False(vm.IsContextFile);
        Assert.True(vm.RescanSelectedFolderCommand.CanExecute(null));
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));

        vm.ContextTarget = NewFileElem(scanRootId: 1, fileIndex: 5);

        Assert.True(vm.HasContextTarget);
        Assert.False(vm.IsContextDir);
        Assert.True(vm.IsContextFile);
        Assert.False(vm.RescanSelectedFolderCommand.CanExecute(null));
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));

        vm.ContextTarget = null;

        Assert.False(vm.HasContextTarget);
        Assert.False(vm.IsContextDir);
        Assert.False(vm.IsContextFile);
        Assert.False(vm.RescanSelectedFolderCommand.CanExecute(null));
        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task RescanSelectedFolder_InvokesScannerWithDirHandle()
    {
        var repo = new FakeRepo([
            NewScanRoot(1, "/root", null, isDeleted: false)
        ]);

        var host = new FakeRepoHost(repo);
        var scanner = new FakeScanCoordinator();
        var dialogs = new FakeDialogService();
        var deleter = new FakeFileSystemDeleteService();

        var vm = new TreeMapActionsViewModel(host, scanner, dialogs, deleter)
        { ContextTarget = NewDirElem(scanRootId: 1, dirIndex: 42) };

        await vm.RescanSelectedFolderCommand.ExecuteAsync(null);

        Assert.Single(scanner.RescannedFolders);
        Assert.Equal(new DirHandle(1, 42), scanner.RescannedFolders[0]);
    }

    [Fact]
    public async Task DeleteSelected_Dir_WhenCantResolveRelPath_ShowsError_NoConfirm()
    {
        var repo = new FakeRepo([NewScanRoot(1, "/root", null, isDeleted: false)]);

        var host = new FakeRepoHost(repo)
        {
            FileDirIndex = new FakeFileDirReadModel
            {
                TryGetDirPathByHandleImpl = (_, out rel) =>
                {
                    rel = "";
                    return false;
                }
            }
        };

        var vm = new TreeMapActionsViewModel(host, new FakeScanCoordinator(), new FakeDialogService(), new FakeFileSystemDeleteService())
        {
            ContextTarget = NewDirElem(scanRootId: 1, dirIndex: 10)
        };

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Contains(VmDialogs(vm).Errors, e => e.Title == "Delete failed" && e.Message.Contains("resolve folder path", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(VmDialogs(vm).Confirmations);
        Assert.Empty(VmDeleter(vm).DeletedDirectories);
        Assert.Empty(repo.DeletedDirs);
    }

    [Fact]
    public async Task DeleteSelected_Dir_WhenRootMissing_ShowsError()
    {
        var repo = new FakeRepo([NewScanRoot(99, "/other", null, isDeleted: false)]);

        var host = new FakeRepoHost(repo)
        {
            FileDirIndex = new FakeFileDirReadModel
            {
                TryGetDirPathByHandleImpl = (_, out rel) =>
                {
                    rel = "sub";
                    return true;
                }
            }
        };

        var vm = new TreeMapActionsViewModel(host, new FakeScanCoordinator(), new FakeDialogService(), new FakeFileSystemDeleteService())
        {
            ContextTarget = NewDirElem(scanRootId: 1, dirIndex: 10) // scan root 1 not in map
        };

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Contains(VmDialogs(vm).Errors, e => e.Title == "Delete failed" && e.Message.Contains("scan root path", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(VmDialogs(vm).Confirmations);
        Assert.Empty(VmDeleter(vm).DeletedDirectories);
        Assert.Empty(repo.DeletedDirs);
    }

    [Fact]
    public async Task DeleteSelected_Dir_WhenUserCancels_DoesNothing()
    {
        var repo = new FakeRepo([NewScanRoot(1, "/root", null, isDeleted: false)]);

        var host = new FakeRepoHost(repo)
        {
            FileDirIndex = new FakeFileDirReadModel
            {
                TryGetDirPathByHandleImpl = (_, out rel) =>
                {
                    rel = "sub";
                    return true;
                }
            }
        };

        var dialogs = new FakeDialogService { NextConfirmResult = false };
        var deleter = new FakeFileSystemDeleteService();
        var vm = new TreeMapActionsViewModel(host, new FakeScanCoordinator(), dialogs, deleter) { ContextTarget = NewDirElem(scanRootId: 1, dirIndex: 10) };

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Confirmations);
        Assert.Empty(deleter.DeletedDirectories);
        Assert.Empty(repo.DeletedDirs);
    }

    [Fact]
    public async Task DeleteSelected_Dir_WhenDiskDeleteFails_ShowsError_DoesNotCallRepo()
    {
        var repo = new FakeRepo([NewScanRoot(1, "/root", null, isDeleted: false)]);

        var host = new FakeRepoHost(repo)
        {
            FileDirIndex = new FakeFileDirReadModel
            {
                TryGetDirPathByHandleImpl = (_, out rel) =>
                {
                    rel = "sub";
                    return true;
                }
            }
        };

        var dialogs = new FakeDialogService { NextConfirmResult = true };
        var deleter = new FakeFileSystemDeleteService { NextDeleteDirectoryResult = (false, "nope") };

        var vm = new TreeMapActionsViewModel(host, new FakeScanCoordinator(), dialogs, deleter) { ContextTarget = NewDirElem(scanRootId: 1, dirIndex: 10) };

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Confirmations);
        Assert.Contains(dialogs.Errors, e => e.Title == "Delete failed" && e.Message.Contains("nope", StringComparison.OrdinalIgnoreCase));
        Assert.Single(deleter.DeletedDirectories);
        Assert.Empty(repo.DeletedDirs);
    }

    [Fact]
    public async Task DeleteSelected_Dir_Success_DeletesDisk_ThenCallsRepoDelete()
    {
        var repo = new FakeRepo([NewScanRoot(1, "/root", null, isDeleted: false)]);

        var host = new FakeRepoHost(repo)
        {
            FileDirIndex = new FakeFileDirReadModel
            {
                TryGetDirPathByHandleImpl = (_, out rel) =>
                {
                    rel = "sub";
                    return true;
                }
            }
        };

        var dialogs = new FakeDialogService { NextConfirmResult = true };
        var deleter = new FakeFileSystemDeleteService { NextDeleteDirectoryResult = (true, null) };

        var vm = new TreeMapActionsViewModel(host, new FakeScanCoordinator(), dialogs, deleter);

        var handle = new DirHandle(1, 10);
        vm.ContextTarget = NewDirElem(handle);

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Confirmations);

        var deletedPath = deleter.DeletedDirectories.Single().Path.Replace('\\', '/');
        Assert.Equal("/root/sub", deletedPath);

        Assert.Single(repo.DeletedDirs);
        Assert.Equal(handle, repo.DeletedDirs[0]);

        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task DeleteSelected_File_Success_DeletesDisk_ThenCallsRepoDelete()
    {
        var repo = new FakeRepo([NewScanRoot(1, "/root", null, isDeleted: false)]);

        var host = new FakeRepoHost(repo)
        {
            FileDirIndex = new FakeFileDirReadModel
            {
                TryGetFilePathByHandleImpl = (_, out rel) =>
                {
                    rel = "a.bin";
                    return true;
                }
            }
        };

        var dialogs = new FakeDialogService { NextConfirmResult = true };
        var deleter = new FakeFileSystemDeleteService { NextDeleteFileResult = (true, null) };

        var vm = new TreeMapActionsViewModel(host, new FakeScanCoordinator(), dialogs, deleter);

        var handle = new FileHandle(1, 7);
        vm.ContextTarget = NewFileElem(handle);

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Confirmations);

        var deletedPath = deleter.DeletedFiles.Single().Replace('\\', '/');
        Assert.Equal("/root/a.bin", deletedPath);

        Assert.Single(repo.DeletedFiles);
        Assert.Equal(handle, repo.DeletedFiles[0]);

        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task IndexesRebuilt_RebuildsScanRootPaths_UsedByDelete()
    {
        var repo = new FakeRepo([NewScanRoot(1, "/old", null, isDeleted: false)]);

        var host = new FakeRepoHost(repo)
        {
            FileDirIndex = new FakeFileDirReadModel
            {
                TryGetFilePathByHandleImpl = (_, out rel) =>
                {
                    rel = "a.bin";
                    return true;
                }
            }
        };

        var dialogs = new FakeDialogService { NextConfirmResult = true };
        var deleter = new FakeFileSystemDeleteService { NextDeleteFileResult = (true, null) };

        var vm = new TreeMapActionsViewModel(host, new FakeScanCoordinator(), dialogs, deleter);

        // Update scan root path, notify
        repo.SetScanRoots([NewScanRoot(1, "/new", null, isDeleted: false)]);
        host.RaiseIndexesRebuilt();

        vm.ContextTarget = NewFileElem(scanRootId: 1, fileIndex: 7);

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        var deletedPath = deleter.DeletedFiles.Single().Replace('\\', '/');
        Assert.Equal("/new/a.bin", deletedPath);
    }

    // ---------------------------------------------------------------------
    // Element construction (proper constructors)
    // ---------------------------------------------------------------------

    private static readonly FakeTreeMapDataResolver s_resolver = new();

    private static DirTreeMapElement NewDirElem(long scanRootId, int dirIndex)
        => NewDirElem(new DirHandle(scanRootId, dirIndex));

    private static DirTreeMapElement NewDirElem(DirHandle h)
        => new(s_resolver, h, NewScanRoot(h.ScanRootId, "/root", null, isDeleted: false), value: 1);

    private static FileTreeMapElement NewFileElem(long scanRootId, int fileIndex)
        => NewFileElem(new FileHandle(scanRootId, fileIndex));

    private static FileTreeMapElement NewFileElem(FileHandle h)
        => new(s_resolver, h, NewScanRoot(h.ScanRootId, "/root", null, isDeleted: false), value: 123);

    private static ScanRoot NewScanRoot(long rootId, string rootPath, string? volumePath, bool isDeleted)
        => new()
        {
            RootId = rootId,
            RootPath = rootPath,
            VolumePath = volumePath,
            IsDeleted = isDeleted,
            DirId = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static FakeDialogService VmDialogs(TreeMapActionsViewModel vm)
        => (FakeDialogService)typeof(TreeMapActionsViewModel)
            .GetField("_dialogs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(vm)!;

    private static FakeFileSystemDeleteService VmDeleter(TreeMapActionsViewModel vm)
        => (FakeFileSystemDeleteService)typeof(TreeMapActionsViewModel)
            .GetField("_deleter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(vm)!;
}
