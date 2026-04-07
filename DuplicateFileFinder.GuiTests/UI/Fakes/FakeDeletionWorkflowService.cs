using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using DuplicateFileFinder.Gui.Application.Deletion;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.GuiTests.UI.Fakes;

public sealed class FakeDeletionWorkflowService : IDeletionWorkflowService
{
    public DeleteItemResult NextResult { get; set; } =
        new(false, DeleteItemFailure.CancelledByUser);

    public List<(FileHandle File, string FullPath)> DeleteFileCalls { get; } = [];
    public List<(DirHandle File, string FullPath)> DeleteFolderCalls { get; } = [];

    public Task<DeleteItemResult> DeleteFileAsync(FileHandle fileHandle, string fullPath, CancellationToken ct = default)
    {
        DeleteFileCalls.Add((fileHandle, fullPath));
        return Task.FromResult(NextResult);
    }

    public Task<DeleteItemResult> DeleteFolderAsync(DirHandle dirHandle, string fullPath, CancellationToken ct = default)
    {
        DeleteFolderCalls.Add((dirHandle, fullPath));
        return Task.FromResult(NextResult);
    }
}
