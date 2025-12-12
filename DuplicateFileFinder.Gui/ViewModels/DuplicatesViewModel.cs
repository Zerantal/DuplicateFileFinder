// ViewModels/DuplicatesViewModel.cs

using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DuplicateFileFinder.Gui.Controls;
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

    private readonly IScanCoordinator _scanner;
    [ObservableProperty] private int _duplicatesFound;
    [ObservableProperty] private int _filesScanned;

    // Selected folder prefix (full path). Null/empty = no folder filter.
    private string? _selectedFolderPrefix;

    private DuplicateSetRow? _selectedSet;
    [ObservableProperty] private long _wastedBytes;

    // Root node for the TreeMap (file-size based)
    [ObservableProperty] private TreeMapNode? _directoryTreeMapRoot;
    private readonly ITreeIndexReadModel _treeIndex;
    
    private sealed record TreeMapBuildOptions
    {
        public int  MaxDepth          { get; init; } = 8;     // depth relative to each scan-root
        public int  MaxSubdirsPerDir  { get; init; } = 32;    // keep top N subdirs by total bytes
        public int  MaxFilesPerDir    { get; init; } = 64;    // keep top M files by size
        public bool DirectoriesOnly   { get; init; }         // skip file rectangles entirely
    }

    private readonly TreeMapBuildOptions _treeMapOptions = new()
    {
        // tune here (or later expose via settings)
        MaxDepth = 6,
        MaxSubdirsPerDir = 32,
        MaxFilesPerDir = 64,
        DirectoriesOnly = false
    };

// BuildDirectoryTreeMap caches (per-build; kept as fields to avoid re-alloc churn if you want)
    private readonly Dictionary<long, long> _dirTotalBytesCache = new();

    public DuplicatesViewModel(IRepoHost host, IScanCoordinator scanner)
    {
        ArgumentNullException.ThrowIfNull(host);

        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _repo = host.Repo;
        _hashIndexService = host.HashIndex;
        _treeIndex = host.TreeIndex;

        LoadFromRepo();
    }

    public BulkObservableCollection<DuplicateSetRow> FilteredSets { get; } = [];

    public ObservableCollection<FolderNodeViewModel> FolderRoots { get; } = [];


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
        using (TimingLog.StartPhase("BuildDirectoryTreeMap()")) BuildDirectoryTreeMap(snapshot);
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
                    list = [];
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

        var sortedList = filteredSets
            .OrderByDescending(r => r.TotalBytes)
            .ToList();

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
    
private void BuildDirectoryTreeMap(IRepoView snapshot)
{
    _dirTotalBytesCache.Clear();

    var opts = _treeMapOptions;

    static bool IsLive(ScanEntryStatus s) => s is not (ScanEntryStatus.Deleted or ScanEntryStatus.None);

    long GetDirTotalBytes(long dirId)
    {
        if (_dirTotalBytesCache.TryGetValue(dirId, out var cached))
            return cached;

        if (!snapshot.Dirs.TryGetValue(dirId, out var dir) || !IsLive(dir.Status))
            return _dirTotalBytesCache[dirId] = 0;

        long sum = 0;

        // Direct files in dir
        foreach (var fileId in _treeIndex.GetChildFileIds(dirId))
        {
            if (!snapshot.Files.TryGetValue(fileId, out var f))
                continue;
            if (!IsLive(f.Status))
                continue;
            if (f.Size > 0)
                sum += f.Size;
        }

        // Subdirs (recursive)
        foreach (var childDirId in _treeIndex.GetChildDirIds(dirId))
        {
            if (!snapshot.Dirs.TryGetValue(childDirId, out var childDir))
                continue;
            if (!IsLive(childDir.Status))
                continue;

            sum += GetDirTotalBytes(childDirId);
        }

        _dirTotalBytesCache[dirId] = sum;
        return sum;
    }

    TreeMapNode BuildDirNode(long dirId, int depth)
    {
        if (!snapshot.Dirs.TryGetValue(dirId, out var dir) || !IsLive(dir.Status))
        {
            return new TreeMapNode
            {
                Label = $"[missing:{dirId}]",
                IsDirectory = true,
                Value = 0,
                Children = []
            };
        }

        // Depth cap: stop expanding, return an aggregated leaf
        if (depth >= opts.MaxDepth)
        {
            return new TreeMapNode
            {
                Label = dir.Name,
                IsDirectory = true,
                Value = GetDirTotalBytes(dirId), // leaf value
                Children = [],
                Fill = null
            };
        }

        var children = new List<TreeMapNode>();

        // ---- Subdirectories (keep top N by total bytes) ----
        var subdirs = new List<(long Id, long Bytes)>();
        foreach (var childDirId in _treeIndex.GetChildDirIds(dirId))
        {
            if (!snapshot.Dirs.TryGetValue(childDirId, out var childDir) || !IsLive(childDir.Status))
                continue;

            subdirs.Add((childDirId, GetDirTotalBytes(childDirId)));
        }

        subdirs.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        long otherDirsBytes = 0;
        int otherDirsCount = 0;

        for (int i = 0; i < subdirs.Count; i++)
        {
            var (childId, bytes) = subdirs[i];

            if (i < opts.MaxSubdirsPerDir)
            {
                // Only recurse into kept subdirs
                children.Add(BuildDirNode(childId, depth + 1));
            }
            else
            {
                otherDirsBytes += bytes;
                otherDirsCount++;
            }
        }

        if (otherDirsCount > 0 && otherDirsBytes > 0)
        {
            children.Add(new TreeMapNode
            {
                Label = $"Other dirs ({otherDirsCount})",
                IsDirectory = true,
                Value = otherDirsBytes,
                Children = [],
                Fill = null
            });
        }

        // ---- Files (keep top M by size) ----
        if (!opts.DirectoriesOnly)
        {
            var files = new List<FileRecord>();
            foreach (var fileId in _treeIndex.GetChildFileIds(dirId))
            {
                if (!snapshot.Files.TryGetValue(fileId, out var f))
                    continue;
                if (!IsLive(f.Status))
                    continue;
                if (f.Size <= 0)
                    continue;

                files.Add(f);
            }

            files.Sort((a, b) => b.Size.CompareTo(a.Size));

            long otherFilesBytes = 0;
            int otherFilesCount = 0;

            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                if (i < opts.MaxFilesPerDir)
                {
                    children.Add(new TreeMapNode
                    {
                        Label = f.Name,
                        IsDirectory = false,
                        Value = f.Size,
                        Children = [],
                        Fill = null
                    });
                }
                else
                {
                    otherFilesBytes += f.Size;
                    otherFilesCount++;
                }
            }

            if (otherFilesCount > 0 && otherFilesBytes > 0)
            {
                children.Add(new TreeMapNode
                {
                    Label = $"Other files ({otherFilesCount})",
                    IsDirectory = false,
                    Value = otherFilesBytes,
                    Children = [],
                    Fill = null
                });
            }
        }

        return new TreeMapNode
        {
            Label = dir.Name,
            IsDirectory = true,
            Value = 0, // non-leaf: TreeMapControl will aggregate from children
            Children = children,
            Fill = null
        };
    }

    // One treemap child per (live) scan root
    var scanRootNodes = new List<TreeMapNode>();

    foreach (var scanRoot in _repo.ScanRootsView.Where(r => !r.IsDeleted))
    {
        if (!snapshot.Dirs.TryGetValue(scanRoot.DirId, out var rootDir))
            continue;

        if (!IsLive(rootDir.Status))
            continue;

        // depth starts at 0 per scan root
        scanRootNodes.Add(BuildDirNode(scanRoot.DirId, depth: 0));
    }

    if (scanRootNodes.Count == 0)
    {
        DirectoryTreeMapRoot = null;
        return;
    }

    // Colour each first-level scan-root directory differently (unchanged)
    var palette = new[]
    {
        "#FF4E79A7",
        "#FF59A14F",
        "#FFEDC948",
        "#FFB07AA1",
        "#FF9C755F",
        "#FF76B7B2",
        "#FFE15759"
    };

    for (int i = 0; i < scanRootNodes.Count; i++)
    {
        var color = Color.Parse(palette[i % palette.Length]);
        scanRootNodes[i].Fill = new SolidColorBrush(color);
    }

    DirectoryTreeMapRoot = new TreeMapNode
    {
        Label = "All scan roots",
        IsDirectory = true,
        Value = 0,
        Children = scanRootNodes,
        Fill = null
    };
}


}