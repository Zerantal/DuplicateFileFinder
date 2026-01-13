using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;

/// <summary>
/// Application-level actions invoked by scan-roots tree nodes.
/// Keeps FolderNodeViewModel free of direct dependencies on dialogs/filesystem/repo.
/// </summary>
public interface IScanRootsTreeNodeActions
{
    Task RescanScanRootAsync(long scanRootId);
    Task RescanFolderAsync(DirHandle dir);

    /// <summary>Confirm and remove the scan root from the repository.</summary>
    /// <returns>true if the scan root was removed.</returns>
    Task<bool> TryRemoveScanRootAsync(long scanRootId);

    /// <summary>Prompt for and set the scan root display name.</summary>
    /// <returns>true if a change was applied (user didn’t cancel).</returns>
    Task<bool> TrySetScanRootDisplayNameAsync(long scanRootId, string currentLabel);

    /// <summary>Confirm and delete folder from disk, then remove from repo.</summary>
    Task DeleteFolderAsync(DirHandle dir, string fullPath);
}
