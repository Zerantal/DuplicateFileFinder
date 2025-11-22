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

    private readonly IRepo _repo;
    [ObservableProperty] private int _duplicatesFound;
    [ObservableProperty] private int _filesScanned;

    // Selected folder prefix (full path). Null/empty = no folder filter.
    private string? _selectedFolderPrefix;

    private DuplicateSetRow? _selectedSet;
    [ObservableProperty] private long _wastedBytes;

    public DuplicatesViewModel(Repo repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));

        LoadFromRepo();
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
        DuplicatesFound = 0;
        WastedBytes = 0;
        FilesScanned = _files.Count;

        foreach (var (_, ids) in _hashIndex)
        {
            if (ids.Count < 2)
                continue;

            var size = _files[ids[0]].Size;
            var wasted = (ids.Count - 1) * size;
            var stat = new HashStats(1, wasted);

            DuplicatesFound += stat.Groups;
            WastedBytes += stat.WastedBytes;
        }
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

    private DuplicateSetRow BuildRow(HashKey hash, IReadOnlyList<FileRecord> files)
    {
        string PathResolver(FileRecord f)
        {
            var dirPath = _repo.GetFullDirPath(f.DirId);
            return Path.Combine(dirPath, f.Name);
        }

        return new DuplicateSetRow(hash, files, PathResolver);
    }

    private void ApplyFilters()
    {
        var filteredSets = new List<DuplicateSetRow>();

        foreach (var kv in _allSets)
        {
            var row = kv.Value;

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

    public void LoadFromRepo()
    {
        var snap = _repo.GetSnapshot();
        InitializeFromSnapshot(snap);
    }

    // One group per hash, but keeping this explicit makes the math clear
    private readonly record struct HashStats(int Groups, long WastedBytes);
}