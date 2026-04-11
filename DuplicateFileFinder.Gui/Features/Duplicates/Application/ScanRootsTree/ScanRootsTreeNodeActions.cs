using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;

public sealed class ScanRootsTreeNodeActions : IScanRootsTreeNodeActions
{
    private readonly IScanCoordinator _scanner;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;

    public ScanRootsTreeNodeActions(
        IRepoHost host,
        IScanCoordinator scanner,
        IDialogService dialogs,
        IClipboardService clipboard)
    {
        ArgumentNullException.ThrowIfNull(host);
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
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

    public Task CopyPathAsync(string fullPath)
        => string.IsNullOrWhiteSpace(fullPath)
            ? Task.CompletedTask
            : _clipboard.SetTextAsync(fullPath);
}

