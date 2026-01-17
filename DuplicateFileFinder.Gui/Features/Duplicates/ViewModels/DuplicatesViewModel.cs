using CommunityToolkit.Mvvm.ComponentModel;

using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTreeFlat;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;

public partial class DuplicatesViewModel : ObservableObject
{
    private readonly IRepo _repo;
    private readonly TreeMapController _treeMap;

    public ScanRootsFlatTreeViewModel ScanRootsTree { get; }
    public TreeMapActionsViewModel TreeMapActions { get; }
    public DuplicateGroupsViewModel DuplicateGroups { get; }

    public DuplicatesViewModel(
        IRepoHost host,
        ScanRootsFlatTreeViewModel scanRootsTree,
        TreeMapController treeMapController,
        TreeMapActionsViewModel treeMapActions,
        DuplicateGroupsViewModel duplicateGroups)
    {
        ArgumentNullException.ThrowIfNull(host);

        _repo = host.Repo;

        ScanRootsTree = scanRootsTree ?? throw new ArgumentNullException(nameof(scanRootsTree));
        _treeMap = treeMapController ?? throw new ArgumentNullException(nameof(treeMapController));
        TreeMapActions = treeMapActions ?? throw new ArgumentNullException(nameof(treeMapActions));
        DuplicateGroups = duplicateGroups ?? throw new ArgumentNullException(nameof(duplicateGroups));

        // folder selection drives duplicate-filter prefix
        ScanRootsTree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScanRootsFlatTreeViewModel.SelectedPath))
                DuplicateGroups.SelectedFolderPrefix = ScanRootsTree.SelectedPath;
        };

        // treemap selection drives navigation
        _treeMap.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TreeMapController.SelectedNode))
                OnTreeMapSelectionChanged();
        };

        LoadFromRepo();
    }

    private void OnTreeMapSelectionChanged()
    {
        var node = _treeMap.SelectedNode;
        if (node?.Element == null)
            return;

        if (node.Element is DirTreeMapElement dirNode)
        {
            var dir = dirNode.Dir;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => ScanRootsTree.NavigateToDir(dir),
                Avalonia.Threading.DispatcherPriority.Background);
        }
        else if (node.Element is FileTreeMapElement fileNode)
        {
            var file = fileNode.File;
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => ScanRootsTree.NavigateToFile(file),
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    // Expose treemap controller for binding
    public TreeMapController TreeMapController => _treeMap;

    public bool IsTreeMapMetricBytes
    {
        get => _treeMap.IsMetricBytes;
        set
        {
            if (_treeMap.IsMetricBytes == value)
                return;
            _treeMap.IsMetricBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTreeMapMetricFiles));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateBytes));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateFiles));
        }
    }

    public bool IsTreeMapMetricFiles
    {
        get => _treeMap.IsMetricFiles;
        set
        {
            if (_treeMap.IsMetricFiles == value)
                return;
            _treeMap.IsMetricFiles = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTreeMapMetricBytes));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateBytes));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateFiles));
        }
    }

    public bool IsTreeMapMetricDuplicateFiles
    {
        get => _treeMap.IsMetricDuplicateFiles;
        set
        {
            if (_treeMap.IsMetricDuplicateFiles == value)
                return;
            _treeMap.IsMetricDuplicateFiles = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTreeMapMetricBytes));
            OnPropertyChanged(nameof(IsTreeMapMetricFiles));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateBytes));
        }
    }

    public bool IsTreeMapMetricDuplicateBytes
    {
        get => _treeMap.IsMetricDuplicateBytes;
        set
        {
            if (_treeMap.IsMetricDuplicateBytes == value)
                return;
            _treeMap.IsMetricDuplicateBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTreeMapMetricBytes));
            OnPropertyChanged(nameof(IsTreeMapMetricFiles));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateFiles));
        }
    }

    public object MaxDepth => _treeMap.Options.MaxDepth + 2;

    public void LoadFromRepo()
    {
        using (TimingLog.StartPhase("LoadFromRepo()"))
        {
            RepoSnapshotView repoSnapshot = _repo.GetRepoSnapshotView();
            InitializeFromSnapshot(repoSnapshot);
        }
    }

    private void InitializeFromSnapshot(RepoSnapshotView snapshot)
    {
        using (TimingLog.StartPhase("BuildScanRootsTree()"))
        {
            ScanRootsTree.Rebuild(snapshot);
        }

        using (TimingLog.StartPhase("RebuildDuplicatesAndState()"))
        {
            DuplicateGroups.Rebuild(snapshot);
        }

        using (TimingLog.StartPhase("BuildDirectoryTreeMap()"))
        {
            _treeMap.Rebuild(snapshot);
        }
    }
}
