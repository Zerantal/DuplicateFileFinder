using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Application;

public sealed class DuplicateFileDeletionService : IDuplicateFileDeletionService
{
    private readonly IRepo _repo;
    private readonly IFileDirReadModel _fileDirIndex;
    private readonly IDialogService _dialogs;
    private readonly IFileSystemDeleteService _deleter;

    public DuplicateFileDeletionService(
        IRepoHost host,
        IDialogService dialogs,
        IFileSystemDeleteService deleter)
    {
        ArgumentNullException.ThrowIfNull(host);

        _repo = host.Repo;
        _fileDirIndex = host.FileDirIndex;

        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _deleter = deleter ?? throw new ArgumentNullException(nameof(deleter));
    }

    public async Task<DuplicateFileDeletionResult> DeleteAsync(
        long fileId,
        string fullPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return new DuplicateFileDeletionResult(false, DuplicateFileDeletionFailure.FullPathBlank);

        // Confirm
        var ok = await _dialogs.ShowConfirmationAsync(
            title: "Delete file",
            message: $"Delete this file from disk?\n\n{fullPath}",
            okText: "Delete",
            cancelText: "Cancel");

        if (!ok)
            return new DuplicateFileDeletionResult(false, DuplicateFileDeletionFailure.CancelledByUser);

        ct.ThrowIfCancellationRequested();

        // Delete from disk first
        var (deleted, deleteErr) = await _deleter.DeleteFileAsync(fullPath);
        if (!deleted)
        {
            await _dialogs.ShowErrorAsync(
                title: "Delete failed",
                message: deleteErr ?? "Unknown error.");
            return new DuplicateFileDeletionResult(false, DuplicateFileDeletionFailure.FileSystemDeleteFailed);
        }

        ct.ThrowIfCancellationRequested();

        // Resolve handle via index, then delete from repo
        if (!_fileDirIndex.TryGetFile(fileId, out var fileHandle))
        {
            await _dialogs.ShowErrorAsync(
                title: "Delete error",
                message: "Deleted file from disk, but could not resolve the file handle in the index. " +
                         "The repository may still show the file until the next rescan/rebuild.");
            return new DuplicateFileDeletionResult(false, DuplicateFileDeletionFailure.HandleResolutionFailed);
        }

        var repoResult = await _repo.DeleteFileAsync(fileHandle, ct);
        if (!repoResult.Success)
        {
            await _dialogs.ShowErrorAsync(
                title: "Delete error",
                message: $"Deleted file from disk, but deleting entry from repository failed: {repoResult.Error}");
            return new DuplicateFileDeletionResult(false, DuplicateFileDeletionFailure.RepoDeleteFailed);
        }

        return new DuplicateFileDeletionResult(true);
    }
}
