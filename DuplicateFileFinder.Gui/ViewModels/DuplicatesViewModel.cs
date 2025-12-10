// ViewModels/DuplicatesViewModel.cs

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DuplicateFileFinder.Gui.Models;
using DuplicateFileFinder.Gui.Services;
using DuplicateFileFinder.Gui.Util;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.ViewModels;

public partial class DuplicatesViewModel : ObservableObject
{
    private readonly IHashIndexReadModel _hashIndexService;
    private readonly IRepo _repo;
    
    // FullScan universe of duplicate sets keyed by hash
    private readonly Dictionary<HashKey, DuplicateSetRow> _allSets = new();

    // Guid DirId -> DirRecord (for folder tree construction)
    private readonly Dictionary<long, DirRecord> _dirs = new();
    
    // parentDirId -> list of child dir Ids
    private readonly Dictionary<long, List<long>> _childDirIdsByParent = new();

    private readonly Dictionary<long, FolderNodeViewModel> _folderNodes = new();
    // private readonly Dictionary<HashKey, List<Guid>> _hashIndex = new();

    private readonly IScanCoordinator _scanner;
    [ObservableProperty] private int _duplicatesFound;
    [ObservableProperty] private int _filesScanned;

    // Selected folder prefix (full path). Null/empty = no folder filter.
    private string? _selectedFolderPrefix;

    private DuplicateSetRow? _selectedSet;
    [ObservableProperty] private long _wastedBytes;

    public DuplicatesViewModel(IRepoHost host, IScanCoordinator scanner)
    {
        ArgumentNullException.ThrowIfNull(host);

        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _repo = host.Repo;
        _hashIndexService = host.HashIndex;

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

    private void InitializeFromSnapshot(IRepoView snapshot)
    {
        _dirs.Clear();
        _folderNodes.Clear();
        _allSets.Clear();
        FolderRoots.Clear();

        foreach (var (id, dir) in snapshot.Dirs)
            _dirs[id] = dir;

        using (TimingLog.StartPhase("BuildFolderTree()")) BuildFolderTree();
        using (TimingLog.StartPhase("RebuildDuplicatesAndState()")) RebuildDuplicatesAndStats(snapshot);
        using (TimingLog.StartPhase("ApplyFilters()")) ApplyFilters();
    }

    private void RebuildDuplicatesAndStats(IRepoView snapshot)
    {
        DuplicatesFound = 0;
        WastedBytes = 0;
        FilesScanned = snapshot.Files.Count;
        _allSets.Clear();

        int minDuplicates = 2;
        long minSize = 10*1024*1024;
        var duplicateGroups = _hashIndexService.GetDuplicateGroups(minDuplicates, minSize);

        foreach (var group in duplicateGroups)
        {
            // All files in a group share the same hash and size by definition
            try
            {
                // retrieve all FileRecords for files in group
                var fileRecords = group.list.Select(id => snapshot.Files[id]).ToList();
                var hash = fileRecords[0].Hash;
                var row = BuildRow(hash, fileRecords);
                _allSets[hash] = row;
            }
            catch (Exception e)
            {
                // swallow + continue
                Console.Error.WriteLine(e);
            }
        }

        DuplicatesFound = _hashIndexService.TotalDuplicateFileCount;
        WastedBytes = _hashIndexService.TotalSpaceTakenByDuplicates;
    }

    private void BuildFolderTree()
    {
        FolderRoots.Clear();
        _folderNodes.Clear();
        _childDirIdsByParent.Clear();

        // Build a parent -> children index, only for "live" directories
        foreach (var dir in _dirs.Values)
        {
            if (dir.Status == ScanEntryStatus.None)
                continue;

            if (dir.ParentDirId is { } parentId &&
                _dirs.TryGetValue(parentId, out var parentDir) &&
                parentDir.Status != ScanEntryStatus.None)
            {
                if (!_childDirIdsByParent.TryGetValue(parentId, out var list))
                {
                    list = new List<long>();
                    _childDirIdsByParent[parentId] = list;
                }

                list.Add(dir.DirId);
            }
        }

        // Use actual scan roots as the visible roots
        foreach (var scanRoot in _repo.ScanRootsView.Where( r => !r.IsDeleted))
        {
            if (!_dirs.TryGetValue(scanRoot.DirId, out var rootDir))
                continue;

            if (rootDir.Status == ScanEntryStatus.None)
                continue;

            var node = GetOrCreateNode(rootDir.DirId, true);

            node.Parent = null;
            node.ShowFullPath = true;                 // you can also use scanRoot.DisplayName if preferred
            node.OnRootRemoved = n => FolderRoots.Remove(n);
            node.ScanRootId = scanRoot.RootId;

            InsertRootSorted(node);
        }
    }
    
    private FolderNodeViewModel GetOrCreateNode(long dirId, bool isScanRoot = false)
    {
        if (_folderNodes.TryGetValue(dirId, out var existing))
            return existing;

        ScanRoot? scanRoot = isScanRoot ? _repo.ScanRootsView.FirstOrDefault(s => s.DirId == dirId) : null;
        var dir = _dirs[dirId];
        string fullPath;
        
        if (isScanRoot && scanRoot != null)
            fullPath = scanRoot.VolumePath != null ? Path.Combine(scanRoot.VolumePath, scanRoot.RootPath) : scanRoot.RootPath;
        else
            fullPath = _repo.GetFullDirPath(dir.DirId);

        var node = new FolderNodeViewModel(dir.DirId, dir.Name, fullPath, _scanner)
        {
            // this delegate is called by the node when it is expanded
            EnsureChildrenLoaded = EnsureChildrenLoaded
        };

        _folderNodes[dirId] = node;

        // If it has children, add a dummy so the UI shows an expand arrow,
        // but don't actually allocate real child nodes yet.
        if (_childDirIdsByParent.ContainsKey(dir.DirId))
            node.AddDummyChild();

        return node;
    }

    private void EnsureChildrenLoaded(FolderNodeViewModel node)
    {
        // If we already materialised the children, do nothing
        if (!node.HasDummyChild)
            return;

        node.ClearChildren();

        if (!_childDirIdsByParent.TryGetValue(node.DirId, out var childIds))
            return;

        foreach (var childId in childIds)
        {
            var childNode = GetOrCreateNode(childId);
            childNode.Parent = node;
            InsertChildSorted(node, childNode);
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

        foreach (var row in _allSets.Values)
        {
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
        await Task.Run(() => _repo.CompactAsync());

        // After compaction, reload from the repo to reflect any changes
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var snap = _repo.GetRepoView();
            InitializeFromSnapshot(snap);
        });
    }

    public void LoadFromRepo()
    {
        using (TimingLog.StartPhase("LoadFromRepo()"))
        {
            var snap = _repo.GetRepoView();
            InitializeFromSnapshot(snap);
        }
    }
}