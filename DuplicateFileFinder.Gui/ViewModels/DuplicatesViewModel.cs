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
    // private readonly Dictionary<Guid, string> _dirPathCache = new();
    private readonly Dictionary<Guid, DirRecord> _dirs = new();

    // VM-local copies
    private readonly Dictionary<Guid, FileRecord> _files = new();
    
    private readonly Dictionary<Guid, FolderNodeViewModel> _folderNodes = new();
    private readonly Dictionary<HashKey, List<Guid>> _hashIndex = new();

    // Per-hash stats so we can update DuplicatesFound/WastedBytes incrementally
    private readonly Dictionary<HashKey, HashStats> _hashStats = new();
    private readonly IRepo _repo;
    [ObservableProperty] private int _duplicatesFound;

    [ObservableProperty] private int _filesScanned;

    private string? _pathContains;

    // Selected folder prefix (full path). Null/empty = no folder filter.
    private string? _selectedFolderPrefix;

    private DuplicateSetRow? _selectedSet;
    [ObservableProperty] private long _wastedBytes;

    public DuplicatesViewModel(Repo repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));

        var snap = _repo.GetSnapshot();
        InitializeFromSnapshot(snap);

        _repo.DeltaCommitted += OnDeltaCommitted;
    }

    public BulkObservableCollection<DuplicateSetRow> FilteredSets { get; } = new();

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

    public DuplicateSetRow? SelectedSet
    {
        get => _selectedSet;
        set
        {
            if (ReferenceEquals(value, _selectedSet))
                return;

            // clear previous
            if (_selectedSet != null)
                _selectedSet.IsSelected = false;

            _selectedSet = value;

            // mark new
            if (_selectedSet != null)
                _selectedSet.IsSelected = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedItems));
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

        CalculateScanStats();
        BuildFolderTree();
        ApplyFilters();
    }

    private void CalculateScanStats()
    {
        _hashStats.Clear();
        DuplicatesFound = 0;
        WastedBytes = 0;
        FilesScanned = _files.Count;

        foreach (var (hash, ids) in _hashIndex)
        {
            if (ids.Count < 2)
                continue;

            var size = _files[ids[0]].Size;
            var wasted = (ids.Count - 1) * size;
            var stat = new HashStats(1, wasted);
            _hashStats[hash] = stat;

            DuplicatesFound += stat.Groups;
            WastedBytes += stat.WastedBytes;
        }
    }

    private void UpdateStatsForHashes(IEnumerable<HashKey> hashes)
    {
        foreach (var hash in hashes)
        {
            var hadOld = _hashStats.TryGetValue(hash, out var oldStat);
            if (hadOld)
            {
                DuplicatesFound -= oldStat.Groups;
                WastedBytes -= oldStat.WastedBytes;
            }

            if (!_hashIndex.TryGetValue(hash, out var ids) || ids.Count < 2)
            {
                if (hadOld)
                    _hashStats.Remove(hash);
                continue;
            }

            var size = _files[ids[0]].Size;
            var wasted = (ids.Count - 1) * size;
            var newStat = new HashStats(1, wasted);

            _hashStats[hash] = newStat;
            DuplicatesFound += newStat.Groups;
            WastedBytes += newStat.WastedBytes;
        }

        FilesScanned = _files.Count;
    }

    private void BuildFolderTree()
    {
        FolderRoots.Clear();
        _folderNodes.Clear();

        // Create node instances for each directory
        foreach (var dir in _dirs.Values)
        {
            var fullPath = _repo.GetFullDirPath(dir.Id);
            var node = new FolderNodeViewModel(dir.Id, dir.Name, fullPath);
            _folderNodes[dir.Id] = node;
        }

        // Wire up parent/child relationships and decide roots
        foreach (var dir in _dirs.Values)
        {
            var node = _folderNodes[dir.Id];
            if (dir.ParentId is { } parentId && _dirs[parentId].Status != ScanEntryStatus.None &&
                _folderNodes.TryGetValue(parentId, out var parentNode))
            {
                node.Parent = parentNode;
                InsertChildSorted(parentNode, node);
            }
            else
            {
                if (_dirs[node.DirId].Status == ScanEntryStatus.None)
                    continue;

                node.Parent = null;
                node.ShowFullPath = true;
                InsertRootSorted(node);
            }
        }
    }

    private void InsertRootSorted(FolderNodeViewModel node)
    {
        var index = 0;
        while (index < FolderRoots.Count &&
               string.Compare(FolderRoots[index].Name, node.Name, StringComparison.OrdinalIgnoreCase) < 0)
            index++;
        FolderRoots.Insert(index, node);
    }

    private void InsertChildSorted(FolderNodeViewModel parent, FolderNodeViewModel node)
    {
        var children = parent.Children;
        var index = 0;
        while (index < children.Count &&
               string.Compare(children[index].Name, node.Name, StringComparison.OrdinalIgnoreCase) < 0)
            index++;
        children.Insert(index, node);
    }

    private void ApplyDirDelta(RepoDelta delta)
    {
        if (delta.Dirs is { Count: > 0 })
            foreach (var dir in delta.Dirs)
                UpsertFolderNode(dir);

        if (delta.DeletedDirs is { Count: > 0 })
            foreach (var tomb in delta.DeletedDirs)
                RemoveFolderNode(tomb.Id);
    }

    private void UpsertFolderNode(DirRecord dir)
    {
        // Recompute full path from current repo state
        var fullPath = _repo.GetFullDirPath(dir.Id);

        if (!_folderNodes.TryGetValue(dir.Id, out var node))
        {
            node = new FolderNodeViewModel(dir.Id, dir.Name, fullPath);
            _folderNodes[dir.Id] = node;
        }
        else
        {
            node.Name = dir.Name;
            node.FullPath = fullPath;
        }

        // Check if parent changed
        FolderNodeViewModel? newParent = null;
        if (dir.ParentId is { } parentId && _folderNodes.TryGetValue(parentId, out var parent))
            newParent = parent;

        if (node.Parent == newParent)
            return; // no reparenting needed

        // Remove from old parent/root
        if (node.Parent is { } oldParent)
            oldParent.Children.Remove(node);
        else
            FolderRoots.Remove(node);

        // Determine whether this directory should be a tree root
        var isDummy = dir.Status == ScanEntryStatus.None;
        var parentIsReal =
            dir.ParentId is { } pid &&
            _dirs.TryGetValue(pid, out var parentDir) &&
            parentDir.Status != ScanEntryStatus.None;

        var shouldBeRoot = !isDummy && !parentIsReal;

        node.Parent = null;

        // If this is a dummy dir, it should NOT appear anywhere
        if (isDummy)
        {
            // remove node if it was previously present
            FolderRoots.Remove(node);
            if (node.Parent != null)
                node.Parent.Children.Remove(node);
            return;
        }

        // Real directory:
        if (shouldBeRoot)
        {
            node.ShowFullPath = true;
            InsertRootSorted(node);
        }
        else
        {
            // real parent exists → attach as child
            _folderNodes.TryGetValue(dir.ParentId!.Value, out var parentNode);
            node.Parent = parentNode;
            node.ShowFullPath = false;
            InsertChildSorted(parentNode!, node);
        }
    }

    private void RemoveFolderNode(Guid dirId)
    {
        if (!_folderNodes.TryGetValue(dirId, out var node))
            return;

        // Remove subtree from tree
        RemoveNodeRecursive(node);

        // Remove entries from map
        void RemoveNodeRecursive(FolderNodeViewModel n)
        {
            foreach (var child in n.Children.ToArray())
                RemoveNodeRecursive(child);

            if (n.Parent is { } p)
                p.Children.Remove(n);
            else
                FolderRoots.Remove(n);

            _folderNodes.Remove(n.DirId);
        }
    }


    private DuplicateSetRow BuildRow(HashKey hash, IReadOnlyList<FileRecord> files)
    {
        string PathResolver(FileRecord f)
        {
            var dirPath = _repo.GetFullDirPath(f.DirId);
            return Path.Combine(dirPath, f.Name);
        }

        return new DuplicateSetRow(hash, files, PathResolver);
    }

    // Filtering
    private void OnFilterChanged()
    {
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var filteredSets = new List<DuplicateSetRow>();

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

            filteredSets.Add(row);
        }

        var sortedList = filteredSets.OrderByDescending(r => r.TotalBytes);
        FilteredSets.AddRange(sortedList, true);
    }

    private void OnDeltaCommitted(object? sender, RepoDelta delta)
    {
        Dispatcher.UIThread.Post(() => ApplyDelta(delta));
    }

    private void ApplyDelta(RepoDelta delta)
    {
        var touchedHashes = new HashSet<HashKey>();
        var hasDirChanges = false;

        // 1) Files: mirror Repo.ApplyDelta, but against VM copies, not Repo
        foreach (var f in delta.Files)
        {
            // If existing file's hash changed, remove from old hash bucket
            if (_files.TryGetValue(f.Id, out var existing))
                if (!existing.Hash.Equals(f.Hash))
                {
                    if (_hashIndex.TryGetValue(existing.Hash, out var oldList))
                    {
                        oldList.Remove(f.Id);
                        if (oldList.Count == 0)
                            _hashIndex.Remove(existing.Hash);
                    }

                    touchedHashes.Add(existing.Hash);
                }

            _files[f.Id] = f;

            if (!_hashIndex.TryGetValue(f.Hash, out var list))
            {
                list = new List<Guid>(4);
                _hashIndex[f.Hash] = list;
            }

            if (!list.Contains(f.Id))
                list.Add(f.Id);

            touchedHashes.Add(f.Hash);
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

                touchedHashes.Add(file.Hash);
            }

        // 2) Dirs
        foreach(var d in delta.Dirs) 
        {
            _dirs[d.Id] = d;
            hasDirChanges = true;
        }
        if (delta.DeletedDirs is { Count: > 0 })
            foreach(var tomb in delta.DeletedDirs) 
            {
                _dirs.Remove(tomb.Id);
                hasDirChanges = true;
            }

        // 3) Update duplicate sets only for touched hashes
        foreach (var hash in touchedHashes)
            if (_hashIndex.TryGetValue(hash, out var ids) && ids.Count >= 2)
            {
                var filesForHash = ids.Select(id => _files[id]).ToList();
                _allSets[hash] = BuildRow(hash, filesForHash);
            }
            else
            {
                _allSets.Remove(hash);
            }

        // 4) Incremental stats update (global counters, local per-hash stats)
        if (touchedHashes.Count > 0)
            UpdateStatsForHashes(touchedHashes);

        // 5) Folder tree only if directories changed
        if (hasDirChanges)
            ApplyDirDelta(delta);

        // 6) Re-apply filters / virtualized rows
        ApplyFilters();
    }

    
    public async Task OptimizeRepoAsync()
    {
        // Run compaction off the UI thread
        await Task.Run(() => _repo.CompactNow());

        // After compaction, reload from the repo to reflect any changes
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var snap = _repo.GetSnapshot();
            InitializeFromSnapshot(snap);
        });
    }

    // One group per hash, but keeping this explicit makes the math clear
    private readonly record struct HashStats(int Groups, long WastedBytes);
}