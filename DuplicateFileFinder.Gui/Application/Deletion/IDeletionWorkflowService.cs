using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Application.Deletion;

public interface IDeletionWorkflowService
{
    Task<DeleteItemResult> DeleteFileAsync(
        FileHandle fileHandle,
        string fullPath,
        CancellationToken ct = default);

    Task<DeleteItemResult> DeleteFolderAsync(
        DirHandle dirHandle,
        string fullPath,
        CancellationToken ct = default);
}
