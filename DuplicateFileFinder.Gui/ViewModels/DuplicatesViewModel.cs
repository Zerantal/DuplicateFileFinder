// ViewModels/DuplicatesViewModel.cs

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DuplicateFileFinder.Gui.Models;
using DuplicateFileFinder.Gui.Util;
using DuplicateFileFinderLib.Repository;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinder.Gui.ViewModels;

public partial class DuplicatesViewModel : ObservableObject
{
    // Full universe of sets keyed by hash
    private readonly Dictionary<HashKey, DuplicateSetRow> _allSets = new();

    // Guid DirId -> full path cache
    private readonly Dictionary<Guid, string> _dirPathCache = new();
    private readonly Dictionary<Guid, DirRecord> _dirs = new();

    // VM-local copies
    private readonly Dictionary<Guid, FileRecord> _files = new();

    // Filtered key list + how many are currently visible
    private readonly List<HashKey> _filteredHashes = new();
    private readonly Dictionary<HashKey, List<Guid>> _hashIndex = new();
    private readonly IRepo _repo;
    [ObservableProperty] private int _duplicatesFound;

    [ObservableProperty] private int _filesScanned;
    [ObservableProperty] private long _wastedBytes;

    private string? _pathContains;

    // Selected folder prefix (full path). Null/empty = no folder filter.
    private string? _selectedFolderPrefix;

    // Selection / details
    private DuplicateSetRow? _selectedSet;
    private int _visibleCount;

    // “Virtualized” window configuration
    private int _windowSize = 500;

    public DuplicatesViewModel(Repo repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));

        var snap = _repo.GetSnapshot();
        InitializeFromSnapshot(snap);

        _repo.DeltaCommitted += OnDeltaCommitted;
    }

    public ObservableCollection<FolderNodeViewModel> FolderRoots { get; } = new();


    public string? SelectedFolderPrefix
    {
        get => _selectedFolderPrefix;
        set
        {
            if (value == _selectedFolderPrefix) return;
            _selectedFolderPrefix = value;
            ApplyFilters(); // reuse existing filtering pipeline
            OnPropertyChanged();
        }
    }

    public string? PathContains
    {
        get => _pathContains;
        set
        {
            if (value != _pathContains)
            {
                _pathContains = value;
                OnFilterChanged();
            }
        }
    }

    public int WindowSize
    {
        get => _windowSize;
        set
        {
            if (value <= 0) value = 1;
            if (value != _windowSize)
            {
                _windowSize = value;
                ResetVisible();
                OnPropertyChanged();
            }
        }
    }

    public bool CanLoadMore => _visibleCount < _filteredHashes.Count;

    // What the DataGrid actually binds to
    public BulkObservableCollection<DuplicateSetRow> VisibleRows { get; } = new();

    public DuplicateSetRow? SelectedSet
    {
        get => _selectedSet;
        set
        {
            if (!Equals(value, _selectedSet))
            {
                _selectedSet = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedItems));
            }
        }
    }

    public IReadOnlyList<FileItem> SelectedItems =>
        _selectedSet?.Items ?? [];

    private void InitializeFromSnapshot(RepoViewSnapshot snapshot)
    {
        _files.Clear();
        _dirs.Clear();
        _hashIndex.Clear();
        _allSets.Clear();
        _dirPathCache.Clear();
        FolderRoots.Clear();

        foreach (var (id, file) in snapshot.Files)
            _files[id] = file;

        foreach (var (id, dir) in snapshot.Dirs)
            _dirs[id] = dir;

        foreach (var (hash, ids) in snapshot.HashIndex)
        {
            var idList = ids.ToList(); // local copy
            _hashIndex[hash] = idList;

            if (ids.Count >= 2)
            {
                var filesForHash = ids.Select(id => _files[id]).ToList();
                var row = BuildRow(hash, filesForHash);
                _allSets[hash] = row;
            }
        }

        RecalculateScanStats();

        BuildFolderTree();
        ApplyFilters();
    }

    private void RecalculateScanStats()
    {
        FilesScanned = _files.Count;
        var duplicateSets = _hashIndex.Where((k, _) => k.Value.Count > 1).ToList();

        DuplicatesFound = duplicateSets.Count();
        WastedBytes = duplicateSets
            .Sum(kvp =>
                {
                    var ids = kvp.Value;
                    long fileSize = _files[ids[0]].Size;
                    return (ids.Count - 1) * fileSize;
                });
    }

    private void BuildFolderTree()
    {
        FolderRoots.Clear();
        _dirPathCache.Clear();

        // Build nodes for each directory
        var nodeLookup = new Dictionary<Guid, FolderNodeViewModel>();

        foreach (var dir in _dirs.Values)
        {
            var fullPath = GetFullDirPath(dir.Id);
            var node = new FolderNodeViewModel(dir.Id, dir.Name, fullPath);
            nodeLookup[dir.Id] = node;
        }

        // Wire up parent/child relationships
        foreach (var dir in _dirs.Values)
        {
            var node = nodeLookup[dir.Id];
            if (dir.ParentId is { } parentId &&
                nodeLookup.TryGetValue(parentId, out var parentNode))
                parentNode.Children.Add(node);
            else
                // Root directory (no parent)
                FolderRoots.Add(node);
        }

        void SortChildren(FolderNodeViewModel n)
        {
            var sorted = n.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            n.Children.Clear();
            foreach (var c in sorted)
            {
                n.Children.Add(c);
                SortChildren(c);
            }
        }

        foreach (var root in FolderRoots)
            SortChildren(root);
    }

    private DuplicateSetRow BuildRow(HashKey hash, IReadOnlyList<FileRecord> files)
    {
        string PathResolver(FileRecord f)
        {
            var dirPath = GetFullDirPath(f.DirId);
            return Path.Combine(dirPath, f.Name);
        }

        return new DuplicateSetRow(hash, files, PathResolver);
    }

    private string GetFullDirPath(Guid dirId)
    {
        if (_dirPathCache.TryGetValue(dirId, out var cached))
            return cached;

        if (!_dirs.TryGetValue(dirId, out var node))
            throw new KeyNotFoundException($"Dir {dirId} not found in VM dirs.");

        var parts = new List<string>();

        var cursor = node;

        while (true)
        {
            parts.Add(cursor.Name);

            if (cursor.ParentId is { } parentId)
            {
                if (!_dirs.TryGetValue(parentId, out cursor))
                    throw new InvalidOperationException($"Broken parent chain at {parentId}");
            }
            else
            {
                break;
            }
        }

        parts.Reverse();

        string fullPath;
        if (OperatingSystem.IsWindows())
            fullPath = Path.Combine(parts.ToArray());
        else
            fullPath = Path.DirectorySeparatorChar + Path.Combine(parts.ToArray());

        _dirPathCache[dirId] = fullPath;
        return fullPath;
    }

    // Filtering
    private void OnFilterChanged()
    {
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        _filteredHashes.Clear();

        foreach (var kv in _allSets)
        {
            var row = kv.Value;

            if (!string.IsNullOrWhiteSpace(PathContains))
            {
                var needle = PathContains!;
                if (!row.Items.Any(i =>
                        i.FullPath.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
            }

            if (!string.IsNullOrEmpty(SelectedFolderPrefix))
            {
                var prefix = SelectedFolderPrefix!;
                // Only include sets that have at least one file under the selected folder prefix
                if (!row.Items.Any(i =>
                        i.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }

            _filteredHashes.Add(kv.Key);
        }

        ResetVisible();
    }

    // Reset to first window of filtered results
    private void ResetVisible()
    {
        _visibleCount = 0;
        VisibleRows.BeginUpdate();
        try
        {
            VisibleRows.Clear();
            LoadMoreInternal(); // fill first window
        }
        finally
        {
            VisibleRows.EndUpdate();
        }

        OnPropertyChanged(nameof(CanLoadMore));
    }

    public void LoadMore()
    {
        if (!CanLoadMore) return;

        VisibleRows.BeginUpdate();
        try
        {
            LoadMoreInternal();
        }
        finally
        {
            VisibleRows.EndUpdate();
        }

        OnPropertyChanged(nameof(CanLoadMore));
    }

    private void LoadMoreInternal()
    {
        var remaining = _filteredHashes.Count - _visibleCount;
        if (remaining <= 0) return;

        var take = Math.Min(WindowSize, remaining);
        for (var i = 0; i < take; i++)
        {
            var hash = _filteredHashes[_visibleCount + i];
            if (_allSets.TryGetValue(hash, out var row))
                VisibleRows.Add(row);
        }

        _visibleCount += take;
    }


    private void OnDeltaCommitted(object? sender, RepoDelta delta)
    {
        Dispatcher.UIThread.Post(() => ApplyDelta(delta));
    }

    private void ApplyDelta(RepoDelta delta)
    {
        // 1) Files: mirror Repo.ApplyDelta, but against VM copies, not Repo
        foreach (var f in delta.Files)
        {
            // If existing file's hash changed, remove from old hash bucket
            if (_files.TryGetValue(f.Id, out var existing))
                if (!existing.Hash.Equals(f.Hash))
                    if (_hashIndex.TryGetValue(existing.Hash, out var oldList))
                    {
                        oldList.Remove(f.Id);
                        if (oldList.Count == 0)
                            _hashIndex.Remove(existing.Hash);
                    }

            _files[f.Id] = f;

            if (!_hashIndex.TryGetValue(f.Hash, out var list))
            {
                list = new List<Guid>(4);
                _hashIndex[f.Hash] = list;
            }

            if (!list.Contains(f.Id))
                list.Add(f.Id);
        }

        if (delta.DeletedFiles is { Count: > 0 })
            foreach (var tomb in delta.DeletedFiles)
            {
                if (!_files.TryGetValue(tomb.Id, out var file))
                    continue;

                if (_hashIndex.TryGetValue(file.Hash, out var list))
                {
                    list.Remove(tomb.Id);
                    if (list.Count == 0)
                        _hashIndex.Remove(file.Hash);
                }

                _files.Remove(tomb.Id);
            }

        // 2) Dirs
        foreach (var d in delta.Dirs)
        {
            _dirs[d.Id] = d;
            _dirPathCache.Remove(d.Id);
        }

        if (delta.DeletedDirs is { Count: > 0 })
            foreach (var tomb in delta.DeletedDirs)
            {
                _dirs.Remove(tomb.Id);
                _dirPathCache.Remove(tomb.Id);
            }

        // 3) Rebuild duplicate sets from VM's own hash index
        foreach (var (hash, ids) in _hashIndex)
            if (ids.Count >= 2)
            {
                var filesForHash = ids.Select(id => _files[id]).ToList();
                _allSets[hash] = BuildRow(hash, filesForHash);
            }
            else
            {
                _allSets.Remove(hash);
            }

        // 4) Folder tree + filters
        RecalculateScanStats();
        BuildFolderTree();
        ApplyFilters();
    }
}