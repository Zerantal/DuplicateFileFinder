// DuplicateFileFinder.GuiTests/Features/Duplicates/DuplicateGroupsViewModelDeletionTests.cs

using System;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Application.Deletion;
using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Repository.Core.Models;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates.DuplicateGroups;

public sealed class DuplicateGroupsViewModelDeletionTests
{
    [Fact]
    public async Task DeleteSelectedDuplicateFile_OnSuccess_ClearsSelectedDuplicateFile_AndCallsService()
    {
        var env = CreateSut();
        env.DeleteWorkflowService.NextResult = new DeleteItemResult(true);

        var expectedHandle = new FileHandle(1, 7);
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        env.FileDir.TryGetFileImpl = (fileId, out handle) =>
        {
            Assert.Equal(777, fileId);
            handle = expectedHandle;
            return true;
        };

        env.Vm.SelectedDuplicateFile = MakeFileItem(id: 777, fullPath: "/tmp/a.bin");

        await env.Vm.DeleteSelectedDuplicateFileCommand.ExecuteAsync(null);

        Assert.Single(env.DeleteWorkflowService.DeleteFileCalls);
        Assert.Equal(expectedHandle, env.DeleteWorkflowService.DeleteFileCalls[0].File);
        Assert.Equal("/tmp/a.bin", env.DeleteWorkflowService.DeleteFileCalls[0].FullPath);

        Assert.Null(env.Vm.SelectedDuplicateFile);
    }

    [Fact]
    public async Task DeleteSelectedDuplicateFile_OnFailure_DoesNotClearSelection()
    {
        var env = CreateSut();
        env.DeleteWorkflowService.NextResult = new DeleteItemResult(false, DeleteItemFailure.CancelledByUser);

        var expectedHandle = new FileHandle(1, 7);
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        env.FileDir.TryGetFileImpl = (fileId, out handle) =>
        {
            Assert.Equal(777, fileId);
            handle = expectedHandle;
            return true;
        };

        var item = MakeFileItem(id: 777, fullPath: "/tmp/a.bin");
        env.Vm.SelectedDuplicateFile = item;

        await env.Vm.DeleteSelectedDuplicateFileCommand.ExecuteAsync(null);

        Assert.Single(env.DeleteWorkflowService.DeleteFileCalls);
        Assert.Equal(expectedHandle, env.DeleteWorkflowService.DeleteFileCalls[0].File);
        Assert.Equal("/tmp/a.bin", env.DeleteWorkflowService.DeleteFileCalls[0].FullPath);
        Assert.Equal(item, env.Vm.SelectedDuplicateFile);
    }

    [Fact]
    public async Task DeleteSelectedDuplicateFile_WhenHandleCannotBeResolved_DoesNotCallService_AndDoesNotClearSelection()
    {
        var env = CreateSut();

        env.FileDir.TryGetFileImpl = (_, out handle) =>
        {
            handle = FileHandle.Invalid;
            return false;
        };

        var item = MakeFileItem(id: 777, fullPath: "/tmp/a.bin");
        env.Vm.SelectedDuplicateFile = item;

        await env.Vm.DeleteSelectedDuplicateFileCommand.ExecuteAsync(null);

        Assert.Empty(env.DeleteWorkflowService.DeleteFileCalls);
        Assert.Equal(item, env.Vm.SelectedDuplicateFile);
    }

    [Fact]
    public async Task CopySelectedDuplicateFilePath_CopiesSelectedPath()
    {
        var env = CreateSut();
        env.Vm.SelectedDuplicateFile = MakeFileItem(id: 777, fullPath: "/tmp/a.bin");

        await env.Vm.CopySelectedDuplicateFilePathCommand.ExecuteAsync(null);

        Assert.Equal(["/tmp/a.bin"], env.Clipboard.CopiedTexts);
    }

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    private static FileItem MakeFileItem(FileId id, string fullPath)
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

        var controller = new DuplicateGroupsController(host);
        var deleteSvc = new FakeDeletionWorkflowService();
        var clipboard = new FakeClipboardService();

        var vm = new DuplicateGroupsViewModel(host, controller, deleteSvc, clipboard);

        return new Sut(vm, deleteSvc, fileDir, clipboard);
    }

    private sealed record Sut(
        DuplicateGroupsViewModel Vm,
        FakeDeletionWorkflowService DeleteWorkflowService,
        FakeFileDirReadModel FileDir,
        FakeClipboardService Clipboard);
}
