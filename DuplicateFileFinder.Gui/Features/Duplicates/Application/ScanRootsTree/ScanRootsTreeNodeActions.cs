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

    public Task RescanScanRootAsync(long scanRootId)
        => _scanner.RunRescanLocationWithDialogAsync(scanRootId);

    public Task RescanFolderAsync(DirHandle dir)
        => _scanner.RunFolderRescanWithDialogAsync(dir);

    public async Task<bool> TryRemoveScanRootAsync(long scanRootId)
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

    public async Task<bool> TrySetScanRootDisplayNameAsync(long scanRootId, string currentLabel)
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

        var ok = await _dialogs.ShowConfirmationAsync(
            "Delete folder",
            $"Delete this folder from disk?\n\n{fullPath}",
            okText: "Delete",
            cancelText: "Cancel");

        if (!ok)
            return;

        var (deleted, err) = await _deleter.DeleteDirectoryAsync(fullPath, recursive: true);
        if (!deleted)
        {
            await _dialogs.ShowErrorAsync("Delete failed", err ?? "Unknown error.");
            return;
        }

        var result = await _repo.DeleteDirAsync(dir);
        if (!result.Success)
        {
            await _dialogs.ShowErrorAsync(
                "Delete error",
                $"Deleting entry from repository failed: {result.Error}");
        }
    }
}

