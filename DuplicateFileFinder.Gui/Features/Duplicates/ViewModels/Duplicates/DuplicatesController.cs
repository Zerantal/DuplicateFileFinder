using CommunityToolkit.Mvvm.ComponentModel;

using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.Duplicates;

public partial class DuplicatesController : ObservableObject
{
    private readonly IRepo _repo;
    private readonly IHashIndexReadModel _hashIndex;
    private readonly IFileDirReadModel _mainIndex;

    private readonly Dictionary<HashKey, DuplicateSetRow> _allSets = new();

    private string? _selectedFolderPrefix;
    private DuplicateSetRow? _selectedSet;

    // RootId -> full path (e.g. VolumePath + RootPath)
    private Dictionary<long, string> _scanRootFullPathByRootId = new();

    [ObservableProperty] private int _duplicatesFound;
    [ObservableProperty] private int _filesScanned;
    [ObservableProperty] private long _wastedBytes;

    public BulkObservableCollection<DuplicateSetRow> FilteredSets { get; } = [];

    public DuplicatesController(IRepoHost repoHost, IHashIndexReadModel hashIndex)
    {
        _repo = repoHost.Repo ?? throw new ArgumentNullException(nameof(repoHost));
        _mainIndex = repoHost.FileDirIndex;
        _hashIndex = hashIndex ?? throw new ArgumentNullException(nameof(hashIndex));
    }

    /// <summary>Optional path prefix filter (full path string, case-insensitive).</summary>
    public string? SelectedFolderPrefix
    {
        get => _selectedFolderPrefix;
        set
        {
            if (value == _selectedFolderPrefix)
                return;
            _selectedFolderPrefix = value;
            ApplyFilters();
            OnPropertyChanged();
        }
    }

    public DuplicateSetRow? SelectedSet
    {
        get => _selectedSet;
        set
        {
            if (ReferenceEquals(value, _selectedSet))
                return;

            if (_selectedSet != null)
                _selectedSet.IsSelected = false;

            _selectedSet = value;

            if (_selectedSet != null)
                _selectedSet.IsSelected = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedItems));
        }
    }

    public IReadOnlyList<FileItem> SelectedItems => _selectedSet?.Items ?? [];

    public void Rebuild(RepoSnapshotView snapshot, int minDuplicates = 2, long minSizeBytes = 10 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _allSets.Clear();
        FilteredSets.Clear();

        // Cache scan-root full paths for this rebuild (RootId is the same id used in handles)
        _scanRootFullPathByRootId = _repo.ScanRootsView
            .Where(r => !r.IsDeleted)
            .ToDictionary(
                r => r.RootId,
                r => r.VolumePath != null
                    ? Path.Combine(r.VolumePath, r.RootPath)
                    : r.RootPath);

        FilesScanned = _mainIndex.FileCount;

        // HashIndex provides groups; we map ids -> FileRecord and build DuplicateSetRow
        var groups = _hashIndex.GetDuplicateGroups(minDuplicates, minSizeBytes);

        foreach (var group in groups)
        {
            // group.list is assumed to be file id
            List<(FileRecordV2 FileRecord, string Name, Func<string> pathResolver)> fileRecords;
            try
            {
                // Defensive: only include handles from non-deleted scan roots (in case an index lags).
                fileRecords = group.list
                    .Where(handle => _scanRootFullPathByRootId.ContainsKey(handle.ScanRootId))
                    .Select(handle =>
                {
                    var rec = snapshot.GetFileRecord(handle);
                    var name = snapshot.DecodeFileName(handle);
                    var pathResolver = () =>
                    {
                        // FileDirIndexPlugin returns scan-root-relative path; convert to full path.
                        if (!_mainIndex.TryGetFilePathByHandle(handle, out var relativePath) ||
                            string.IsNullOrWhiteSpace(relativePath))
                        {
                            // Fall back to name (still usable in UI).
                            return name;
                        }

                        if (!_scanRootFullPathByRootId.TryGetValue(handle.ScanRootId, out var rootFullPath) ||
                            string.IsNullOrWhiteSpace(rootFullPath))
                        {
                            // No root info => keep relative.
                            return relativePath;
                        }

                        return Path.Combine(rootFullPath, relativePath);
                    };
                    return (rec, name, pathResolver);
                }).ToList();
            }
            catch
            {
                // Snapshot may not contain file id (stale group); skip.
                continue;
            }

            // Never show deleted files in the duplicates view.
            fileRecords = fileRecords
                .Where(fr => fr.FileRecord.Status != ScanEntryStatus.Deleted)
                .ToList();

            if (fileRecords.Count == 0)
                continue;

            var hash = fileRecords[0].FileRecord.Hash;

            // If duplicates come back in multiple groups for any reason, last wins.
            _allSets[hash] = new DuplicateSetRow(fileRecords);
        }

        DuplicatesFound = _hashIndex.TotalDuplicateFileCount;
        WastedBytes = _hashIndex.TotalSpaceTakenByDuplicates;

        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var filtered = new List<DuplicateSetRow>(_allSets.Count);

        foreach (var row in _allSets.Values)
        {
            if (!string.IsNullOrEmpty(_selectedFolderPrefix))
            {
                var prefix = _selectedFolderPrefix!;
                if (!row.Items.Any(i => i.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            filtered.Add(row);
        }

        // Sort by your existing preference (total bytes desc)
        var sorted = filtered
            .OrderByDescending(r => r.TotalBytes)
            .ToList();

        FilteredSets.AddRange(sorted, clearCollection: true);
    }
}
