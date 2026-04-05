using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Application;

public sealed class DuplicateFileDeletionService : IDuplicateFileDeletionService
{
    private static readonly TimeSpan s_deleteTimeout = TimeSpan.FromMinutes(1);

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
        FileId fileId,
        string fullPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return new DuplicateFileDeletionResult(false, DuplicateFileDeletionFailure.FullPathBlank);

        DuplicateFileDeletionFailure? failure = null;

        var ok = await _dialogs.ShowActionDialogAsync(
            title: "Delete file",
            message: $"Delete this file from disk?\n\n{fullPath}",
            action: async (dialogCt, setWorkingText) =>
            {
                using var timeoutCts = new CancellationTokenSource(s_deleteTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, dialogCt, timeoutCts.Token);
                var linkedCt = linkedCts.Token;

                try
                {
                    linkedCt.ThrowIfCancellationRequested();

                    setWorkingText("Deleting file from disk...");
                    var (deleted, deleteErr) = await _deleter.DeleteFileAsync(fullPath);
                    if (!deleted)
                    {
                        failure = DuplicateFileDeletionFailure.FileSystemDeleteFailed;
                        return (false, deleteErr ?? "Unknown error.");
                    }

                    linkedCt.ThrowIfCancellationRequested();

                    setWorkingText("Resolving repository entry...");
                    if (!_fileDirIndex.TryGetFile(fileId, out var fileHandle))
                    {
                        failure = DuplicateFileDeletionFailure.HandleResolutionFailed;
                        return (
                            false,
                            "Deleted file from disk, but could not resolve the file handle in the index. " +
                            "The repository may still show the file until the next rescan/rebuild.");
                    }

                    linkedCt.ThrowIfCancellationRequested();

                    setWorkingText("Updating indexes...");
                    var repoResult = await _repo.DeleteFileAsync(fileHandle, linkedCt);
                    if (!repoResult.Success)
                    {
                        failure = DuplicateFileDeletionFailure.RepoDeleteFailed;
                        return (
                            false,
                            $"Deleted file from disk, but deleting entry from repository failed: {repoResult.Error}");
                    }

                    return (true, null);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    failure = DuplicateFileDeletionFailure.FileSystemDeleteFailed;
                    return (false, $"Delete timed out after {s_deleteTimeout.TotalMinutes:0} minutes.");
                }
            },
            okText: "Delete",
            cancelText: "Cancel",
            workingText: "Deleting file...");

        if (ok)
            return new DuplicateFileDeletionResult(true);

        return new DuplicateFileDeletionResult(
            false,
            failure ?? DuplicateFileDeletionFailure.CancelledByUser);
    }
}
