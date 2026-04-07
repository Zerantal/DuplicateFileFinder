// DuplicateFileFinder.GuiTests/Application/Deletion/DeletionWorkflowServiceTests.cs

using System;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Application.Deletion;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Core.Models;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Application.Deletion;

public sealed class DeletionWorkflowServiceTests
{
    // ---------------------------------------------------------------------
    // File deletion tests
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DeleteFileAsync_WhenHandleInvalid_ReturnsFailure_AndDoesNothing()
    {
        var env = CreateSut();

        var result = await env.Svc.DeleteFileAsync(FileHandle.Invalid, "/tmp/a.bin", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DeleteItemFailure.InvalidHandle, result.Failure);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.ActionConfirmations);
        Assert.Empty(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Null(env.Dialogs.LastActionResult);
        Assert.Empty(env.Dialogs.LastActionPhaseTexts);
    }

    [Fact]
    public async Task DeleteFileAsync_WhenPathBlank_ReturnsFailure_AndDoesNothing()
    {
        var env = CreateSut();

        var handle = new FileHandle(1, 1);
        var result = await env.Svc.DeleteFileAsync(handle, fullPath: "", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DeleteItemFailure.FullPathBlank, result.Failure);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.ActionConfirmations);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Empty(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);
        Assert.Empty(env.Dialogs.LastActionPhaseTexts);
    }

    [Fact]
    public async Task DeleteFileAsync_WhenCancelled_DoesNotDelete()
    {
        var env = CreateSut();
        env.Dialogs.NextActionConfirmationResult = false;

        var handle = new  FileHandle(1, 123);
        var result = await env.Svc.DeleteFileAsync(handle, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DeleteItemFailure.CancelledByUser, result.Failure);

        var dlg = Assert.Single(env.Dialogs.ActionConfirmations);
        Assert.Equal("Delete file", dlg.Title);
        Assert.Contains("/tmp/a.bin", dlg.Message, StringComparison.Ordinal);
        Assert.Equal("Delete", dlg.OkText);
        Assert.Equal("Cancel", dlg.CancelText);
        Assert.Equal("Deleting file...", dlg.WorkingText);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Empty(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);
        Assert.Empty(env.Dialogs.LastActionPhaseTexts);
    }

    [Fact]
    public async Task DeleteFileAsync_WhenFsDeleteFails_DoesNotTouchRepo_AndDoesNotShowSeparateErrorPopup()
    {
        var env = CreateSut();
        env.Dialogs.NextActionConfirmationResult = true;
        env.Deleter.NextDeleteFileResult = (ok: false, error: "nope");

        var handle = new  FileHandle(1, 123);
        var result = await env.Svc.DeleteFileAsync(handle, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DeleteItemFailure.FileSystemDeleteFailed, result.Failure);

        Assert.Single(env.Dialogs.ActionConfirmations);
        Assert.Single(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);

        Assert.Equal((false, "nope"), env.Dialogs.LastActionResult);
        Assert.Equal(
            ["Deleting file from disk..."],
            env.Dialogs.LastActionPhaseTexts);
    }

    [Fact]
    public async Task DeleteFileAsync_WhenRepoDeleteFails_DoesNotShowSeparateErrorPopup()
    {
        var env = CreateSut();
        env.Dialogs.NextActionConfirmationResult = true;
        env.Deleter.NextDeleteFileResult = (ok: true, error: null);

        var handle = new FileHandle(ScanRootId: 1, Index: 5);

        env.Repo.ReturnResultFor["DeleteFileAsync"] = DeleteResult.Fail(1, 1, "repo nope");
        var result = await env.Svc.DeleteFileAsync(handle, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DeleteItemFailure.RepoDeleteFailed, result.Failure);

        Assert.Single(env.Dialogs.ActionConfirmations);
        Assert.Single(env.Deleter.DeletedFiles);

        // repo delete attempted (and recorded) even though it failed
        var deleted = Assert.Single(env.Repo.DeletedFiles);
        Assert.Equal(handle, deleted);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);

        Assert.Equal(
            "Deleted file from disk, but deleting entry from repository failed: repo nope",
            env.Dialogs.LastActionResult?.error);
        Assert.Equal(
            [
                "Deleting file from disk...",
                "Updating indexes..."
            ],
            env.Dialogs.LastActionPhaseTexts);
    }

    [Fact]
    public async Task DeleteFileAsync_WhenSuccessful_ReturnsSuccess_AndShowsNoSeparateErrorPopup()
    {
        var env = CreateSut();
        env.Dialogs.NextActionConfirmationResult = true;
        env.Deleter.NextDeleteFileResult = (ok: true, error: null);

        var handle = new FileHandle(ScanRootId: 1, Index: 5);

        var result = await env.Svc.DeleteFileAsync(handle, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Failure);

        var dlg = Assert.Single(env.Dialogs.ActionConfirmations);
        Assert.Equal("Delete file", dlg.Title);
        Assert.Contains("/tmp/a.bin", dlg.Message, StringComparison.Ordinal);
        Assert.Equal("Delete", dlg.OkText);
        Assert.Equal("Cancel", dlg.CancelText);
        Assert.Equal("Deleting file...", dlg.WorkingText);

        Assert.Single(env.Deleter.DeletedFiles);

        var deleted = Assert.Single(env.Repo.DeletedFiles);
        Assert.Equal(handle, deleted);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);

        Assert.Equal((true, null), env.Dialogs.LastActionResult);
        Assert.Equal(
            [
                "Deleting file from disk...",
                "Updating indexes..."
            ],
            env.Dialogs.LastActionPhaseTexts);
    }
    // ---------------------------------------------------------------------
    // Folder deletion tests
    // ---------------------------------------------------------------------
    [Fact]
    public async Task DeleteFolderAsync_WhenCancelled_DoesNothing()
    {
        var env = CreateSut();
        env.Dialogs.NextActionConfirmationResult = false;

        var dir = new DirHandle(1, 10);

        var result = await env.Svc.DeleteFolderAsync(dir, "/tmp/folder", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DeleteItemFailure.CancelledByUser, result.Failure);

        var dlg = Assert.Single(env.Dialogs.ActionConfirmations);
        Assert.Equal("Delete folder", dlg.Title);
        Assert.Contains("/tmp/folder", dlg.Message, StringComparison.Ordinal);
        Assert.Equal("Delete", dlg.OkText);
        Assert.Equal("Cancel", dlg.CancelText);
        Assert.Equal("Deleting folder...", dlg.WorkingText);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Deleter.DeletedDirectories);
        Assert.Empty(env.Repo.DeletedDirs);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Null(env.Dialogs.LastActionResult);
        Assert.Empty(env.Dialogs.LastActionPhaseTexts);

    }

    [Fact]
    public async Task DeleteFolderAsync_WhenFsDeleteFails_DoesNotTouchRepo_AndDoesNotShowSeparateErrorPopup()
    {
        var env = CreateSut();
        env.Dialogs.NextActionConfirmationResult = true;
        env.Deleter.NextDeleteDirectoryResult = (ok: false, error: "nope");

        var dir = new DirHandle(1, 10);

        var result = await env.Svc.DeleteFolderAsync(dir, "/tmp/folder", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DeleteItemFailure.FileSystemDeleteFailed, result.Failure);

        var dlg = Assert.Single(env.Dialogs.ActionConfirmations);
        Assert.Equal("Delete folder", dlg.Title);
        Assert.Contains("/tmp/folder", dlg.Message, StringComparison.Ordinal);
        Assert.Equal("Delete", dlg.OkText);
        Assert.Equal("Cancel", dlg.CancelText);
        Assert.Equal("Deleting folder...", dlg.WorkingText);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Single(env.Deleter.DeletedDirectories);
        Assert.Empty(env.Repo.DeletedDirs);
        Assert.Empty(env.Dialogs.Errors);

        Assert.Equal((false, "nope"), env.Dialogs.LastActionResult);
        Assert.Equal(
            ["Deleting folder from disk..."],
            env.Dialogs.LastActionPhaseTexts);
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenFsDeleteOk_AndRepoDeleteFails_DoesNotShowSeparateErrorPopup()
    {
        var env = CreateSut();
        env.Dialogs.NextActionConfirmationResult = true;
        env.Deleter.NextDeleteDirectoryResult = (ok: true, error: null);

        // Make repo delete fail
        env.Repo.ReturnResultFor["DeleteDirAsync"] = DeleteResult.Fail(gen: 1, rootId: 1, error: "repo nope");

        var dir = new DirHandle(1, 10);

        var result = await env.Svc.DeleteFolderAsync(dir, "/tmp/folder", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DeleteItemFailure.RepoDeleteFailed, result.Failure);

        var dlg = Assert.Single(env.Dialogs.ActionConfirmations);
        Assert.Equal("Delete folder", dlg.Title);
        Assert.Contains("/tmp/folder", dlg.Message, StringComparison.Ordinal);
        Assert.Equal("Delete", dlg.OkText);
        Assert.Equal("Cancel", dlg.CancelText);
        Assert.Equal("Deleting folder...", dlg.WorkingText);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Single(env.Deleter.DeletedDirectories);

        var deleted = Assert.Single(env.Repo.DeletedDirs);
        Assert.Equal(dir, deleted);

        Assert.Empty(env.Dialogs.Errors);

        Assert.Equal(
            (false, "Deleting entry from repository failed: repo nope"),
            env.Dialogs.LastActionResult);
        Assert.Equal(
            [
                "Deleting folder from disk...",
                "Updating indexes..."
            ],
            env.Dialogs.LastActionPhaseTexts);
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenSuccessful_ReturnsSuccess_AndShowsNoSeparateErrorPopup()
    {
        var env = CreateSut();
        env.Dialogs.NextActionConfirmationResult = true;
        env.Deleter.NextDeleteDirectoryResult = (ok: true, error: null);

        var dir = new DirHandle(1, 10);

        var result = await env.Svc.DeleteFolderAsync(dir, "/tmp/folder", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Failure);

        var dlg = Assert.Single(env.Dialogs.ActionConfirmations);
        Assert.Equal("Delete folder", dlg.Title);
        Assert.Contains("/tmp/folder", dlg.Message, StringComparison.Ordinal);
        Assert.Equal("Delete", dlg.OkText);
        Assert.Equal("Cancel", dlg.CancelText);
        Assert.Equal("Deleting folder...", dlg.WorkingText);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Single(env.Deleter.DeletedDirectories);

        var deleted = Assert.Single(env.Repo.DeletedDirs);
        Assert.Equal(dir, deleted);

        Assert.Empty(env.Dialogs.Errors);
        Assert.Equal((true, null), env.Dialogs.LastActionResult);
        Assert.Equal(
            [
                "Deleting folder from disk...",
                "Updating indexes..."
            ],
            env.Dialogs.LastActionPhaseTexts);
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenDirInvalid_DoesNothing()
    {
        var env = CreateSut();

        var result = await env.Svc.DeleteFolderAsync(DirHandle.Invalid, "/tmp/folder", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DeleteItemFailure.InvalidHandle, result.Failure);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.ActionConfirmations);
        Assert.Empty(env.Deleter.DeletedDirectories);
        Assert.Empty(env.Repo.DeletedDirs);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Null(env.Dialogs.LastActionResult);
        Assert.Empty(env.Dialogs.LastActionPhaseTexts);
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenPathBlank_DoesNothing()
    {
        var env = CreateSut();
        var dir = new DirHandle(1, 10);

        var result = await env.Svc.DeleteFolderAsync(dir, "", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DeleteItemFailure.FullPathBlank, result.Failure);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.ActionConfirmations);
        Assert.Empty(env.Deleter.DeletedDirectories);
        Assert.Empty(env.Repo.DeletedDirs);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Null(env.Dialogs.LastActionResult);
        Assert.Empty(env.Dialogs.LastActionPhaseTexts);
    }

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private static Sut CreateSut()
    {
        var repo = new FakeRepo();
        var host = new FakeRepoHost(repo);

        var dialogs = new FakeDialogService();
        var deleter = new FakeFileSystemDeleteService();

        var svc = new DeletionWorkflowService(host, dialogs, deleter);

        return new Sut(svc, repo, dialogs, deleter);
    }

    private sealed record Sut(
        DeletionWorkflowService Svc,
        FakeRepo Repo,
        FakeDialogService Dialogs,
        FakeFileSystemDeleteService Deleter);
}
