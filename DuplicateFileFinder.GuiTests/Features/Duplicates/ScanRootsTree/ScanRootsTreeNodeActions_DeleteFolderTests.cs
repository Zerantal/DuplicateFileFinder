// DuplicateFileFinder.GuiTests/Features/Duplicates/ScanRootsTree/ScanRootsTreeNodeActions_DeleteFolderTests.cs

using System;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Core.Models;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates.ScanRootsTree;

public sealed class ScanRootsTreeNodeActionsDeleteFolderTests
{
    [Fact]
    public async Task DeleteFolderAsync_WhenCancelled_DoesNothing()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = false;

        var dir = new DirHandle(1, 10);

        await env.Actions.DeleteFolderAsync(dir, "/tmp/folder");

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Empty(env.Deleter.DeletedDirectories);
        Assert.Empty(env.Repo.DeletedDirs);
        Assert.Empty(env.Dialogs.Errors);
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenFsDeleteFails_ShowsError_AndDoesNotTouchRepo()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = true;
        env.Deleter.NextDeleteDirectoryResult = (ok: false, error: "nope");

        var dir = new DirHandle(1, 10);

        await env.Actions.DeleteFolderAsync(dir, "/tmp/folder");

        Assert.Single(env.Dialogs.Confirmations);
        Assert.Single(env.Deleter.DeletedDirectories);
        Assert.Empty(env.Repo.DeletedDirs);

        var err = Assert.Single(env.Dialogs.Errors);
        Assert.Equal("Delete failed", err.Title);
        Assert.Contains("nope", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenFsDeleteOk_AndRepoDeleteFails_ShowsError()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = true;
        env.Deleter.NextDeleteDirectoryResult = (ok: true, error: null);

        // Make repo delete fail
        env.Repo.ReturnResultFor["DeleteDirAsync"] = DeleteResult.Fail(gen: 1, rootId: 1, error: "repo nope");

        var dir = new DirHandle(1, 10);

        await env.Actions.DeleteFolderAsync(dir, "/tmp/folder");

        Assert.Single(env.Deleter.DeletedDirectories);
        Assert.Single(env.Repo.DeletedDirs);

        var err = Assert.Single(env.Dialogs.Errors);
        Assert.Equal("Delete error", err.Title);
        Assert.Contains("repo nope", err.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteFolderAsync_WhenFsDeleteOk_AndRepoDeleteOk_NoError()
    {
        var env = CreateSut();
        env.Dialogs.NextConfirmResult = true;
        env.Deleter.NextDeleteDirectoryResult = (ok: true, error: null);

        var dir = new DirHandle(1, 10);

        await env.Actions.DeleteFolderAsync(dir, "/tmp/folder");

        Assert.Single(env.Deleter.DeletedDirectories);

        var deleted = Assert.Single(env.Repo.DeletedDirs);
        Assert.Equal(dir, deleted);

        Assert.Empty(env.Dialogs.Errors);
    }

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private static Sut CreateSut()
    {
        var repo = new FakeRepo([]);
        var host = new FakeRepoHost(repo);

        var dialogs = new FakeDialogService();
        var deleter = new FakeFileSystemDeleteService();

        // Scanner is unused by these tests but required by ctor.
        var scanner = new NoopScanCoordinator();

        var actions = new ScanRootsTreeNodeActions(host, scanner, dialogs, deleter);

        return new Sut(actions, repo, dialogs, deleter);
    }

    private sealed record Sut(
        ScanRootsTreeNodeActions Actions,
        FakeRepo Repo,
        FakeDialogService Dialogs,
        FakeFileSystemDeleteService Deleter);

    private sealed class NoopScanCoordinator : IScanCoordinator
    {
        public bool IsScanning => false;

        public event EventHandler<DuplicateFileFinderLib.Core.DuplicateFileFinderProgressReport>? ProgressChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<ScanCompletedEventArgs>? ScanCompleted { add { } remove { } }
        public event EventHandler<ScanIndexedEventArgs>? ScanIndexed { add { } remove { } }

        public Task RunScanNewLocationWithDialogAsync(string rootPath, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task RunRescanLocationWithDialogAsync(ScanRootId scanRootId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RunFolderRescanWithDialogAsync(DirHandle startDir, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemoveScanRoot(ScanRootId scanRootId)
            => Task.CompletedTask;

        public void CancelScan()
            => throw new NotImplementedException();

        public Task SetScanRootDisplayName(ScanRootId scanRootId, string? displayName)
            => Task.CompletedTask;
    }
}

