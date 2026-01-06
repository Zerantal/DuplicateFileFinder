// ViewModels/DuplicatesViewModel.cs

using CommunityToolkit.Mvvm.ComponentModel;

using DuplicateFileFinder.Gui.Features.Duplicates.Domain;
using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.Duplicates;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;

public partial class DuplicatesViewModel : ObservableObject
{
    private readonly DuplicatesController _duplicates;
    private readonly IRepo _repo;
    private readonly TreeMapController _treeMap;

    public ScanRootsTreeViewModel ScanRootsTree { get; }
    public TreeMapActionsViewModel TreeMapActions { get; }

    public DuplicatesViewModel(IRepoHost host, IScanCoordinator scanner, IDialogService dialogService)
    {
        ArgumentNullException.ThrowIfNull(host);

        _repo = host.Repo;
        var hashIndexService = host.HashIndex;

        // folder view
        var treeBuilder = new ScanRootsTreeBuilder(host, scanner, dialogService);
        ScanRootsTree = new ScanRootsTreeViewModel(treeBuilder);
        ScanRootsTree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScanRootsTreeViewModel.SelectedPath))
                SelectedFolderPrefix = ScanRootsTree.SelectedPath;
        };

        // Treemap
        _treeMap = new TreeMapController(host) { Options = new TreeMapBuildOptions { MaxDepth = 8 } };
        _treeMap.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TreeMapController.SelectedNode))
                OnTreeMapSelectionChanged();
        };

        TreeMapActions = new TreeMapActionsViewModel(scanner);

        _duplicates = new DuplicatesController(host, hashIndexService);
        _duplicates.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(DuplicatesController.DuplicatesFound):
                    OnPropertyChanged(nameof(DuplicatesFound));
                    break;
                case nameof(DuplicatesController.FilesScanned):
                    OnPropertyChanged(nameof(FilesScanned));
                    break;
                case nameof(DuplicatesController.WastedBytes):
                    OnPropertyChanged(nameof(WastedBytes));
                    break;
                case nameof(DuplicatesController.SelectedItems):
                    OnPropertyChanged(nameof(SelectedItems));
                    break;
            }
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

    public DuplicateSetRow? SelectedSet
    {
        get => _duplicates.SelectedSet;
        set => _duplicates.SelectedSet = value;
    }

    public IReadOnlyList<FileItem> SelectedItems => _duplicates.SelectedItems;

    public string? SelectedFolderPrefix
    {
        get => _duplicates.SelectedFolderPrefix;
        set => _duplicates.SelectedFolderPrefix = value;
    }

    public int DuplicatesFound => _duplicates.DuplicatesFound;
    public int FilesScanned => _duplicates.FilesScanned;
    public long WastedBytes => _duplicates.WastedBytes;

    public BulkObservableCollection<DuplicateSetRow> FilteredSets => _duplicates.FilteredSets;

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
            _duplicates.Rebuild(snapshot);
        }

        using (TimingLog.StartPhase("BuildDirectoryTreeMap()"))
        {
            _treeMap.Rebuild(snapshot);
        }

        OnPropertyChanged(nameof(FilteredSets));
    }

}
