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
        Assert.Empty(env.Dialogs.Errors);
        Assert.Empty(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);
    }

    [Fact]
    public async Task DeleteAsync_WhenCancelled_DoesNotDelete()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = false;

        var result = await env.Svc.DeleteAsync(fileId: 123, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DuplicateFileDeletionFailure.CancelledByUser, result.Failure);

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Empty(env.Dialogs.Errors);
        Assert.Empty(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);
    }

    [Fact]
    public async Task DeleteAsync_WhenFsDeleteFails_ShowsError_AndDoesNotTouchRepo()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = true;
        env.Deleter.NextDeleteFileResult = (ok: false, error: "nope");

        var result = await env.Svc.DeleteAsync(fileId: 123, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DuplicateFileDeletionFailure.FileSystemDeleteFailed, result.Failure);

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Single(env.Deleter.DeletedFiles);
        Assert.Single(env.Dialogs.Errors);
        Assert.Empty(env.Repo.DeletedFiles);
    }

    [Fact]
    public async Task DeleteAsync_FsDeleteOk_TryGetFileFails_DoesNotCallRepoDelete_AndShowsError()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = true;
        env.Deleter.NextDeleteFileResult = (ok: true, error: null);

        env.FileDir.TryGetFileImpl = (_, out h) =>
        {
            h = FileHandle.Invalid;
            return false;
        };

        var result = await env.Svc.DeleteAsync(fileId: 777, fullPath: "/tmp/a.bin", ct: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DuplicateFileDeletionFailure.HandleResolutionFailed, result.Failure);

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Single(env.Deleter.DeletedFiles);
        Assert.Empty(env.Repo.DeletedFiles);

        var err = Assert.Single(env.Dialogs.Errors);
        Assert.Equal("Delete error", err.Title);
        Assert.Contains("could not resolve", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_FsDeleteOk_TryGetFileSucceeds_RepoDeleteFails_ShowsError()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = true;
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

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Single(env.Deleter.DeletedFiles);

        // repo delete attempted (and recorded) even though it failed
        var deleted = Assert.Single(env.Repo.DeletedFiles);
        Assert.Equal(handle, deleted);

        var err = Assert.Single(env.Dialogs.Errors);
        Assert.Equal("Delete error", err.Title);
        Assert.Contains("failed", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_Success_DeletesFromDisk_ResolvesHandle_DeletesFromRepo_ShowsNoError()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = true;
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

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Single(env.Deleter.DeletedFiles);

        var deleted = Assert.Single(env.Repo.DeletedFiles);
        Assert.Equal(expectedHandle, deleted);

        Assert.Empty(env.Dialogs.Errors);
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

