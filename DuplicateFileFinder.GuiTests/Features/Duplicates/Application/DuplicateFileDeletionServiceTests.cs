// DuplicateFileFinder.GuiTests/Features/Duplicates/Application/DuplicateFileDeletionServiceTests.cs

using System;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Core.Models;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates.Application;

public sealed class DuplicateFileDeletionServiceTests
{
    [Fact]
    public async Task DeleteAsync_WhenPathBlank_ReturnsFailure_AndDoesNothing()
    {
        var env = CreateSut();

        var result = await env.Svc.DeleteAsync(fileId: 1, fullPath: "", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DuplicateFileDeletionFailure.FullPathBlank, result.Failure);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.ProgressConfirmations);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Empty(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);
        Assert.Empty(env.Dialogs.LastProgressPhaseTexts);
    }

    [Fact]
    public async Task DeleteAsync_WhenCancelled_DoesNotDelete()
    {
        var env = CreateSut();
        env.Dialogs.NextProgressConfirmationResult = false;

        var result = await env.Svc.DeleteAsync(fileId: 123, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DuplicateFileDeletionFailure.CancelledByUser, result.Failure);

        var dlg = Assert.Single(env.Dialogs.ProgressConfirmations);
        Assert.Equal("Delete file", dlg.Title);
        Assert.Contains("/tmp/a.bin", dlg.Message, StringComparison.Ordinal);
        Assert.Equal("Delete", dlg.OkText);
        Assert.Equal("Cancel", dlg.CancelText);
        Assert.Equal("Deleting file...", dlg.WorkingText);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Empty(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);
        Assert.Empty(env.Dialogs.LastProgressPhaseTexts);
    }

    [Fact]
    public async Task DeleteAsync_WhenFsDeleteFails_DoesNotTouchRepo_AndDoesNotShowSeparateErrorPopup()
    {
        var env = CreateSut();
        env.Dialogs.NextProgressConfirmationResult = true;
        env.Deleter.NextDeleteFileResult = (ok: false, error: "nope");

        var result = await env.Svc.DeleteAsync(fileId: 123, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DuplicateFileDeletionFailure.FileSystemDeleteFailed, result.Failure);

        Assert.Single(env.Dialogs.ProgressConfirmations);
        Assert.Single(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);

        Assert.Equal((false, "nope"), env.Dialogs.LastProgressActionResult);
        Assert.Equal(
            ["Deleting file from disk..."],
            env.Dialogs.LastProgressPhaseTexts);
    }

    [Fact]
    public async Task DeleteAsync_FsDeleteOk_TryGetFileFails_DoesNotCallRepoDelete_AndDoesNotShowSeparateErrorPopup()
    {
        var env = CreateSut();
        env.Dialogs.NextProgressConfirmationResult = true;
        env.Deleter.NextDeleteFileResult = (ok: true, error: null);

        env.FileDir.TryGetFileImpl = (_, out h) =>
        {
            h = FileHandle.Invalid;
            return false;
        };

        var result = await env.Svc.DeleteAsync(fileId: 777, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DuplicateFileDeletionFailure.HandleResolutionFailed, result.Failure);

        Assert.Single(env.Dialogs.ProgressConfirmations);
        Assert.Single(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);

        Assert.Equal(
            "Deleted file from disk, but could not resolve the file handle in the index. " +
            "The repository may still show the file until the next rescan/rebuild.",
            env.Dialogs.LastProgressActionResult?.error);
        Assert.Equal(
            [
                "Deleting file from disk...",
                "Resolving repository entry..."
            ],
            env.Dialogs.LastProgressPhaseTexts);
    }

    [Fact]
    public async Task DeleteAsync_FsDeleteOk_TryGetFileSucceeds_RepoDeleteFails_DoesNotShowSeparateErrorPopup()
    {
        var env = CreateSut();
        env.Dialogs.NextProgressConfirmationResult = true;
        env.Deleter.NextDeleteFileResult = (ok: true, error: null);

        var handle = new FileHandle(ScanRootId: 1, Index: 5);

        env.FileDir.TryGetFileImpl = (_, out h) =>
        {
            h = handle;
            return true;
        };

        env.Repo.ReturnResultFor["DeleteFileAsync"] = DeleteResult.Fail(1, 1, "repo nope");

        var result = await env.Svc.DeleteAsync(fileId: 777, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DuplicateFileDeletionFailure.RepoDeleteFailed, result.Failure);

        Assert.Single(env.Dialogs.ProgressConfirmations);
        Assert.Single(env.Deleter.DeletedFiles);

        // repo delete attempted (and recorded) even though it failed
        var deleted = Assert.Single(env.Repo.DeletedFiles);
        Assert.Equal(handle, deleted);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);

        Assert.Equal(
            "Deleted file from disk, but deleting entry from repository failed: repo nope",
            env.Dialogs.LastProgressActionResult?.error);
        Assert.Equal(
            [
                "Deleting file from disk...",
                "Resolving repository entry...",
                "Updating indexes..."
            ],
            env.Dialogs.LastProgressPhaseTexts);
    }

    [Fact]
    public async Task DeleteAsync_Success_DeletesFromDisk_ResolvesHandle_DeletesFromRepo_ShowsNoSeparateErrorPopup()
    {
        var env = CreateSut();
        env.Dialogs.NextProgressConfirmationResult = true;
        env.Deleter.NextDeleteFileResult = (ok: true, error: null);

        var expectedHandle = new FileHandle(ScanRootId: 1, Index: 5);

        env.FileDir.TryGetFileImpl = (_, out h) =>
        {
            h = expectedHandle;
            return true;
        };

        var result = await env.Svc.DeleteAsync(fileId: 777, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Failure);

        var dlg = Assert.Single(env.Dialogs.ProgressConfirmations);
        Assert.Equal("Delete file", dlg.Title);
        Assert.Contains("/tmp/a.bin", dlg.Message, StringComparison.Ordinal);
        Assert.Equal("Delete", dlg.OkText);
        Assert.Equal("Cancel", dlg.CancelText);
        Assert.Equal("Deleting file...", dlg.WorkingText);

        Assert.Single(env.Deleter.DeletedFiles);

        var deleted = Assert.Single(env.Repo.DeletedFiles);
        Assert.Equal(expectedHandle, deleted);

        Assert.Empty(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);

        Assert.Equal((true, null), env.Dialogs.LastProgressActionResult);
        Assert.Equal(
            [
                "Deleting file from disk...",
                "Resolving repository entry...",
                "Updating indexes..."
            ],
            env.Dialogs.LastProgressPhaseTexts);
    }

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private static Sut CreateSut()
    {
        var repo = new FakeRepo();
        var host = new FakeRepoHost(repo);

        var fileDir = new FakeFileDirReadModel();
        host.FileDirIndex = fileDir;

        var dialogs = new FakeDialogService();
        var deleter = new FakeFileSystemDeleteService();

        var svc = new DuplicateFileDeletionService(host, dialogs, deleter);

        return new Sut(svc, repo, fileDir, dialogs, deleter);
    }

    private sealed record Sut(
        DuplicateFileDeletionService Svc,
        FakeRepo Repo,
        FakeFileDirReadModel FileDir,
        FakeDialogService Dialogs,
        FakeFileSystemDeleteService Deleter);
}

