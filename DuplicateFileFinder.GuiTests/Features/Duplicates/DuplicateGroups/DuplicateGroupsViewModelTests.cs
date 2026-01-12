// DuplicateFileFinder.GuiTests/Features/Duplicates/DuplicateGroupsViewModelDeletionTests.cs

using System;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Features.Duplicates.DuplicateGroups;

public sealed class DuplicateGroupsViewModelDeletionTests
{
    [Fact]
    public async Task DeleteSelectedDuplicateFile_OnSuccess_ClearsSelectedDuplicateFile_AndCallsService()
    {
        var env = CreateSut();
        env.DeleteService.NextResult = new DuplicateFileDeletionResult(true);

        env.Vm.SelectedDuplicateFile = MakeFileItem(id: 777, fullPath: "/tmp/a.bin");

        await env.Vm.DeleteSelectedDuplicateFileCommand.ExecuteAsync(null);

        Assert.Single(env.DeleteService.Calls);
        Assert.Equal(777, env.DeleteService.Calls[0].FileId);
        Assert.Equal("/tmp/a.bin", env.DeleteService.Calls[0].FullPath);

        Assert.Null(env.Vm.SelectedDuplicateFile);
    }

    [Fact]
    public async Task DeleteSelectedDuplicateFile_OnFailure_DoesNotClearSelection()
    {
        var env = CreateSut();
        env.DeleteService.NextResult = new DuplicateFileDeletionResult(false, DuplicateFileDeletionFailure.CancelledByUser);

        var item = MakeFileItem(id: 777, fullPath: "/tmp/a.bin");
        env.Vm.SelectedDuplicateFile = item;

        await env.Vm.DeleteSelectedDuplicateFileCommand.ExecuteAsync(null);

        Assert.Single(env.DeleteService.Calls);
        Assert.Equal(item, env.Vm.SelectedDuplicateFile);
    }

    // ---------------------------------------------------------------------
    // Harness
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

        var controller = new DuplicateGroupsController(host);

        var deleteSvc = new FakeDuplicateFileDeletionService();

        var vm = new DuplicateGroupsViewModel(controller, deleteSvc);

        return new Sut(vm, deleteSvc);
    }

    private sealed record Sut(
        DuplicateGroupsViewModel Vm,
        FakeDuplicateFileDeletionService DeleteService);

}

