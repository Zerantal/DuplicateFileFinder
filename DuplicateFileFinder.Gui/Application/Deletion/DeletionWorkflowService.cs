using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinder.Gui.Application.Deletion;

public sealed class DeletionWorkflowService : IDeletionWorkflowService
{
    private static readonly TimeSpan s_deleteTimeout = TimeSpan.FromMinutes(1);

    private readonly IRepo _repo;
    private readonly IDialogService _dialogs;
    private readonly IFileSystemDeleteService _deleter;

    public DeletionWorkflowService(
        IRepoHost host,
        IDialogService dialogs,
        IFileSystemDeleteService deleter)
    {
        ArgumentNullException.ThrowIfNull(host);

        _repo = host.Repo;
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _deleter = deleter ?? throw new ArgumentNullException(nameof(deleter));
    }

    public async Task<DeleteItemResult> DeleteFileAsync(
        FileHandle fileHandle,
        string fullPath,
        CancellationToken ct = default)
    {
        if (!fileHandle.IsValid)
            return new DeleteItemResult(false, DeleteItemFailure.InvalidHandle);

        if (string.IsNullOrWhiteSpace(fullPath))
            return new DeleteItemResult(false, DeleteItemFailure.FullPathBlank);

        DeleteItemFailure? failure = null;

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
                        failure = DeleteItemFailure.FileSystemDeleteFailed;
                        return (false, deleteErr ?? "Unknown error.");
                    }

                    linkedCt.ThrowIfCancellationRequested();

                    setWorkingText("Updating indexes...");
                    var repoResult = await _repo.DeleteFileAsync(fileHandle, linkedCt);
                    if (!repoResult.Success)
                    {
                        failure = DeleteItemFailure.RepoDeleteFailed;
                        return (
                            false,
                            $"Deleted file from disk, but deleting entry from repository failed: {repoResult.Error}");
                    }

                    return (true, null);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    failure = DeleteItemFailure.Timeout;
                    return (false, $"Delete timed out after {s_deleteTimeout.TotalMinutes:0} minutes.");
                }
            },
            okText: "Delete",
            cancelText: "Cancel",
            workingText: "Deleting file...");

        if (ok)
            return new DeleteItemResult(true);

        return new DeleteItemResult(false, failure ?? DeleteItemFailure.CancelledByUser);
    }

    public async Task<DeleteItemResult> DeleteFolderAsync(
        DirHandle dirHandle,
        string fullPath,
        CancellationToken ct = default)
    {
        if (!dirHandle.IsValid)
            return new DeleteItemResult(false, DeleteItemFailure.InvalidHandle);

        if (string.IsNullOrWhiteSpace(fullPath))
            return new DeleteItemResult(false, DeleteItemFailure.FullPathBlank);

        DeleteItemFailure? failure = null;

        var ok = await _dialogs.ShowActionDialogAsync(
            title: "Delete folder",
            message: $"Delete this folder from disk?\n\n{fullPath}",
            action: async (dialogCt, setWorkingText) =>
            {
                using var timeoutCts = new CancellationTokenSource(s_deleteTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, dialogCt, timeoutCts.Token);
                var linkedCt = linkedCts.Token;

                try
                {
                    linkedCt.ThrowIfCancellationRequested();

                    setWorkingText("Deleting folder from disk...");
                    var (deleted, deleteErr) = await _deleter.DeleteDirectoryAsync(fullPath, recursive: true);
                    if (!deleted)
                    {
                        failure = DeleteItemFailure.FileSystemDeleteFailed;
                        return (false, deleteErr ?? "Unknown error.");
                    }

                    linkedCt.ThrowIfCancellationRequested();

                    setWorkingText("Updating indexes...");
                    var repoResult = await _repo.DeleteDirAsync(dirHandle, linkedCt);
                    if (!repoResult.Success)
                    {
                        failure = DeleteItemFailure.RepoDeleteFailed;
                        return (false, $"Deleting entry from repository failed: {repoResult.Error}");
                    }

                    return (true, null);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    failure = DeleteItemFailure.Timeout;
                    return (false, $"Delete timed out after {s_deleteTimeout.TotalMinutes:0} minutes.");
                }
            },
            okText: "Delete",
            cancelText: "Cancel",
            workingText: "Deleting folder...");

        if (ok)
            return new DeleteItemResult(true);

        return new DeleteItemResult(false, failure ?? DeleteItemFailure.CancelledByUser);
    }
}
