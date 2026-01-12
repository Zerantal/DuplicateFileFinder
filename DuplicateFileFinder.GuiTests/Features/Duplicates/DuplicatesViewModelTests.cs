// DuplicateFileFinder.GuiTests/Features/Controller/DuplicatesViewModelTests.cs

using System;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;

using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Core.Models;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates;

public sealed class DuplicatesViewModelTests
{
    [Fact]
    public void DeleteSelectedDuplicateFileCommand_CanExecute_DependsOnSelection()
    {
        var env = CreateSut();

        Assert.False(env.Vm.DeleteSelectedDuplicateFileCommand.CanExecute(null));

        env.Vm.SelectedDuplicateFile = MakeFileItem(id: 1, fullPath: "/tmp/a.bin");

        Assert.True(env.Vm.DeleteSelectedDuplicateFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteSelectedDuplicateFile_WhenNoSelection_DoesNothing()
    {
        var env = CreateSut();
        env.Vm.SelectedDuplicateFile = null;

        await env.Vm.DeleteSelectedDuplicateFileCommand.ExecuteAsync(null);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Empty(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);
    }

    [Fact]
    public async Task DeleteSelectedDuplicateFile_WhenPathBlank_DoesNothing()
    {
        var env = CreateSut();
        env.Vm.SelectedDuplicateFile = MakeFileItem(id: 1, fullPath: "");

        await env.Vm.DeleteSelectedDuplicateFileCommand.ExecuteAsync(null);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Empty(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);
    }

    [Fact]
    public async Task DeleteSelectedDuplicateFile_WhenCancelled_DoesNotDelete()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = false;

        env.Vm.SelectedDuplicateFile = MakeFileItem(id: 123, fullPath: "/tmp/a.bin");

        await env.Vm.DeleteSelectedDuplicateFileCommand.ExecuteAsync(null);

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Empty(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);
    }

    [Fact]
    public async Task DeleteSelectedDuplicateFile_WhenFsDeleteFails_ShowsError_DoesNotTouchRepo()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = true;
        env.Deleter.NextDeleteFileResult = (ok: false, error: "nope");

        env.Vm.SelectedDuplicateFile = MakeFileItem(id: 123, fullPath: "/tmp/a.bin");

        await env.Vm.DeleteSelectedDuplicateFileCommand.ExecuteAsync(null);

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Single(env.Deleter.DeletedFiles);
        Assert.Single(env.Dialogs.Errors);

        Assert.Empty(env.Repo.DeletedFiles);
    }

    [Fact]
    public async Task DeleteSelectedDuplicateFile_FsDeleteOk_TryGetFileFails_DoesNotCallRepoDelete_AndShowsError()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = true;
        env.Deleter.NextDeleteFileResult = (ok: true, error: null);

        // TryGetFile fails => repo delete is NOT attempted.
        env.FileDir.TryGetFileImpl = (_, out h) =>
        {
            h = FileHandle.Invalid;
            return false;
        };

        env.Vm.SelectedDuplicateFile = MakeFileItem(id: 777, fullPath: "/tmp/a.bin");

        await env.Vm.DeleteSelectedDuplicateFileCommand.ExecuteAsync(null);

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Single(env.Deleter.DeletedFiles);

        // repo delete is NOT called if handle can't be resolved.
        Assert.Empty(env.Repo.DeletedFiles);

        // shows a clear error explaining the repo may still show the file.
        var err = Assert.Single(env.Dialogs.Errors);
        Assert.Equal("Delete error", err.Title);
        Assert.Contains("could not resolve", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteSelectedDuplicateFile_FsDeleteOk_TryGetFileSucceeds_CallsRepoDelete_WithResolvedHandle_AndShowsNoError()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = true;
        env.Deleter.NextDeleteFileResult = (ok: true, error: null);

        var expectedHandle = new FileHandle(ScanRootId: 1, Index: 5);

        // TryGetFile succeeds => repo delete is attempted with resolved handle.
        env.FileDir.TryGetFileImpl = (_, out h) =>
        {
            h = expectedHandle;
            return true;
        };

        env.Vm.SelectedDuplicateFile = MakeFileItem(id: 777, fullPath: "/tmp/a.bin");

        await env.Vm.DeleteSelectedDuplicateFileCommand.ExecuteAsync(null);

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Single(env.Deleter.DeletedFiles);

        var deleted = Assert.Single(env.Repo.DeletedFiles);
        Assert.Equal(expectedHandle, deleted);

        Assert.Empty(env.Dialogs.Errors);

        Assert.Null(env.Vm.SelectedDuplicateFile);
    }


    // ---------------------------------------------------------------------
    // Test harness
    // ---------------------------------------------------------------------

    private static FileItem MakeFileItem(long id, string fullPath)
        => new()
        {
            Id = id,
            Name = System.IO.Path.GetFileName(fullPath),
            FullPath = fullPath,
            Size = 123,
            Modified = DateTimeOffset.UtcNow
        };

    private static Sut CreateSut()
    {
        var repo = new FakeRepo();
        var host = new FakeRepoHost(repo);

        var fileDir = new FakeFileDirReadModel();
        host.FileDirIndex = fileDir;

        var dialogs = new FakeDialogService();
        var deleter = new FakeFileSystemDeleteService();
        var scanner = new FakeScanCoordinator();

        var vm = new DuplicatesViewModel(host, scanner, dialogs, deleter);

        return new Sut(vm, repo, fileDir, dialogs, deleter);
    }

    private sealed record Sut(
        DuplicatesViewModel Vm,
        FakeRepo Repo,
        FakeFileDirReadModel FileDir,
        FakeDialogService Dialogs,
        FakeFileSystemDeleteService Deleter);

}
