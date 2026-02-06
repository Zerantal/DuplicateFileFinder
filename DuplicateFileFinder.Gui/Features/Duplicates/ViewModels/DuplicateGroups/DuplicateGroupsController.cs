// Features/Controller/ViewModels/Controller/DuplicateGroupsController.cs

using CommunityToolkit.Mvvm.ComponentModel;

using DuplicateFileFinder.Gui.Features.Duplicates.Models;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;

/// <summary>
/// UI-side controller that materializes file details only for the currently selected duplicate group.
/// Paging (which groups are visible) is handled by <see cref="IHashIndexReadModel"/>.
/// </summary>
public sealed partial class DuplicateGroupsController(IRepoHost repoHost) : ObservableObject
{
    private readonly IRepo _repo = repoHost.Repo ?? throw new ArgumentNullException(nameof(repoHost));
    private readonly IHashIndexReadModel _hashIndex = repoHost.HashIndex;
    private readonly IFileDirReadModel _fileDirIndex = repoHost.FileDirIndex;

    private RepoSnapshotView? _snapshot;

    // RootId -> full path (e.g. VolumePath + RootPath)
    private Dictionary<ScanRootId, string> _scanRootFullPathByRootId = new();

    [ObservableProperty] private int _duplicatesFound;
    [ObservableProperty] private int _filesScanned;
    [ObservableProperty] private long _wastedBytes;

    public void Rebuild(RepoSnapshotView snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        _scanRootFullPathByRootId = _repo.ScanRootsView
            .Where(r => !r.IsDeleted)
            .ToDictionary(
                r => r.RootId,
                r => r.VolumePath != null ? Path.Combine(r.VolumePath, r.RootPath) : r.RootPath);

        FilesScanned = _fileDirIndex.FileCount;
        DuplicatesFound = _hashIndex.TotalDuplicateFileCount;
        WastedBytes = _hashIndex.TotalSpaceTakenByDuplicates;
    }

    /// <summary>
    /// Resolve the file list for a specific duplicate group into display-ready <see cref="FileItem"/>s.
    /// Only called when the user selects a group.
    /// </summary>
    public IReadOnlyList<FileItem> ResolveFiles(HashGroupDescriptor descriptor)
    {
        if (_snapshot is null)
            return Array.Empty<FileItem>();

        var handles = _hashIndex.GetGroupFiles(descriptor);
        if (handles.Length == 0)
            return Array.Empty<FileItem>();

        var items = new List<FileItem>(handles.Length);

        for (var i = 0; i < handles.Length; i++)
        {
            var h = handles[i];

            // If we don't have scan root info (deleted root or index lag), drop the row.
            if (!_scanRootFullPathByRootId.TryGetValue(h.ScanRootId, out var rootFullPath))
                continue;

            FileRecordV2 rec;
            string name;
            try
            {
                rec = _snapshot.GetFileRecord(h);
                if (rec.Status == ScanEntryStatus.Deleted)
                    continue;

                name = _snapshot.DecodeFileName(h);
            }
            catch
            {
                // Stale handle / out of bounds; skip.
                continue;
            }

            // FileDirIndex returns scan-root-relative path; convert to full path.
            string fullPath;
            if (_fileDirIndex.TryGetFilePathByHandle(h, out var relativePath) &&
                !string.IsNullOrWhiteSpace(relativePath))
            {
                fullPath = Path.Combine(rootFullPath, relativePath);
            }
            else
            {
                // Fall back to name (still usable in UI).
                fullPath = name;
            }

            items.Add(new FileItem(
                rec.FileId,
                name,
                fullPath,
                rec.Size,
                new DateTimeOffset(rec.ModifiedTicks, TimeSpan.Zero)));
        }

        return items;
    }

    public string? ResolveFileName(FileHandle handle) => _snapshot?.DecodeFileName(handle);
}
