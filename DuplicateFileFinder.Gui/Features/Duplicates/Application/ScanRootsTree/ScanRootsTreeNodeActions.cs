using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;

public sealed class ScanRootsTreeNodeActions : IScanRootsTreeNodeActions
{
    private readonly IScanCoordinator _scanner;
    private readonly IDialogService _dialogs;
    private readonly IFileSystemDeleteService _deleter;
    private readonly IRepo _repo;

    private static readonly TimeSpan s_deleteTimeout = TimeSpan.FromMinutes(1);

    public ScanRootsTreeNodeActions(
        IRepoHost host,
        IScanCoordinator scanner,
        IDialogService dialogs,
        IFileSystemDeleteService deleter)
    {
        ArgumentNullException.ThrowIfNull(host);
        _repo = host.Repo;
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _deleter = deleter ?? throw new ArgumentNullException(nameof(deleter));
    }

    public Task RescanScanRootAsync(ScanRootId scanRootId)
        => _scanner.RunRescanLocationWithDialogAsync(scanRootId);

    public Task RescanFolderAsync(DirHandle dir)
        => _scanner.RunFolderRescanWithDialogAsync(dir);

    public async Task<bool> TryRemoveScanRootAsync(ScanRootId scanRootId)
    {
        var ok = await _dialogs.ShowConfirmationAsync(
            "Remove scan root",
            "Remove this scan root from the repository?",
            okText: "Remove",
            cancelText: "Cancel");

        if (!ok)
            return false;

        await _scanner.RemoveScanRoot(scanRootId);
        return true;
    }

    public async Task<bool> TrySetScanRootDisplayNameAsync(ScanRootId scanRootId, string currentLabel)
    {
        var input = await _dialogs.ShowTextInputAsync(
            "Set display name",
            "Enter a display name for this scan root (blank = clear).",
            currentLabel);

        if (input is null)
            return false;

        var normalized = string.IsNullOrWhiteSpace(input) ? null : input;
        await _scanner.SetScanRootDisplayName(scanRootId, normalized);
        return true;
    }

    public async Task DeleteFolderAsync(DirHandle dir, string fullPath)
    {
        if (!dir.IsValid || string.IsNullOrWhiteSpace(fullPath))
            return;

        await _dialogs.ShowActionDialogAsync(
            "Delete folder",
            $"Delete this folder from disk?\n\n{fullPath}",
            async (dialogCt, setWorkingText) =>
            {
                using var timeoutCts = new CancellationTokenSource(s_deleteTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(dialogCt, timeoutCts.Token);
                var linkedCt = linkedCts.Token;

                try
                {
                    linkedCt.ThrowIfCancellationRequested();

                    setWorkingText("Deleting folder from disk...");
                    var (deleted, err) = await _deleter.DeleteDirectoryAsync(fullPath, recursive: true);
                    if (!deleted)
                        return (false, err ?? "Unknown error.");

                    linkedCt.ThrowIfCancellationRequested();

                    setWorkingText("Updating indexes...");
                    var result = await _repo.DeleteDirAsync(dir, linkedCt);
                    if (!result.Success)
                        return (false, $"Deleting entry from repository failed: {result.Error}");

                    return (true, null);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    return (false, $"Delete timed out after {s_deleteTimeout.TotalMinutes:0} minutes.");
                }
            },
            okText: "Delete",
            cancelText: "Cancel",
            workingText: "Deleting folder...");
    }
}

