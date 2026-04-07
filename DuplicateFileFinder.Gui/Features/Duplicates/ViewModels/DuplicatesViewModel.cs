using CommunityToolkit.Mvvm.ComponentModel;

using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
// ReSharper disable PartialTypeWithSinglePart

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;

public partial class DuplicatesViewModel : ObservableObject
{
    private readonly IRepo _repo;

    private bool _syncingSelection;

    public ScanRootsTreeViewModel ScanRootsTree { get; }
    public TreeMapActionsViewModel TreeMapActions { get; }
    public DuplicateGroupsViewModel DuplicateGroups { get; }

    public DuplicatesViewModel(
        IRepoHost host,
        ScanRootsTreeViewModel scanRootsTree,
        TreeMapController treeMapController,
        TreeMapActionsViewModel treeMapActions,
        DuplicateGroupsViewModel duplicateGroups)
    {
        ArgumentNullException.ThrowIfNull(host);

        _repo = host.Repo;

        ScanRootsTree = scanRootsTree ?? throw new ArgumentNullException(nameof(scanRootsTree));
        TreeMapController = treeMapController ?? throw new ArgumentNullException(nameof(treeMapController));
        TreeMapActions = treeMapActions ?? throw new ArgumentNullException(nameof(treeMapActions));
        DuplicateGroups = duplicateGroups ?? throw new ArgumentNullException(nameof(duplicateGroups));

        // treemap selection drives navigation
        TreeMapController.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TreeMapController.SelectedNode))
                OnTreeMapSelectionChanged();
        };

        // scan-roots selection drives treemap sync + duplicates subtree filter
        ScanRootsTree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ScanRootsTree.SelectedRow)) return;
            OnScanRootsTreeSelectionChanged();
            OnScanRootsTreeSelectionChanged_DuplicatesFilter();
        };

        LoadFromRepo();
    }

    private void OnScanRootsTreeSelectionChanged_DuplicatesFilter()
    {
        var row = ScanRootsTree.SelectedRow;

        if (row?.Dir is { IsValid: true } dir)
            DuplicateGroups.SelectedSubtreeDir = dir;
        else
            DuplicateGroups.SelectedSubtreeDir = null;
    }

    private void OnScanRootsTreeSelectionChanged()
    {
        if (_syncingSelection)
            return;

        var row = ScanRootsTree.SelectedRow;
        if (row is null)
        {
            _syncingSelection = true;
            try { TreeMapController.SelectedNode = null; }
            finally { _syncingSelection = false; }
            return;
        }

        // Nearest-ancestor fallback:
        // try the selected row; if it isn't present in the treemap (depth cap, metric pruning, etc),
        // walk up parents until we find something that exists.
        var model = row.Model;
        TreeMapNode<ITreeMapNodeElement>? target = null;

        while (model is not null)
        {
            var dir = model.Dir;
            if (dir.IsValid && TreeMapController.DirNodeByHandle.TryGetValue(dir, out var node))
            {
                target = node;
                break;
            }

            model = model.Parent;
        }

        _syncingSelection = true;
        try
        {
            // Prefer setting on UI thread to keep property-changed ordering consistent with view updates
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => TreeMapController.SelectedNode = target, // may be null if nothing found
                Avalonia.Threading.DispatcherPriority.Background);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnTreeMapSelectionChanged()
    {
        if (_syncingSelection)
            return;

        var node = TreeMapController.SelectedNode;
        if (node?.Element == null)
            return;

        _syncingSelection = true;
        try
        {
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
            else if (node.Element is SyntheticTreeMapElement { ParentDir: not null } otherNode)
            {
                var dir = otherNode.ParentDir.Value;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => ScanRootsTree.NavigateToDir(dir),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    // Expose treemap controller for binding
    public TreeMapController TreeMapController { get; }

    public bool IsTreeMapMetricBytes
    {
        get => TreeMapController.IsMetricBytes;
        set
        {
            if (TreeMapController.IsMetricBytes == value)
                return;
            TreeMapController.IsMetricBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTreeMapMetricFiles));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateBytes));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateFiles));
        }
    }

    public bool IsTreeMapMetricFiles
    {
        get => TreeMapController.IsMetricFiles;
        set
        {
            if (TreeMapController.IsMetricFiles == value)
                return;
            TreeMapController.IsMetricFiles = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTreeMapMetricBytes));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateBytes));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateFiles));
        }
    }

    public bool IsTreeMapMetricDuplicateFiles
    {
        get => TreeMapController.IsMetricDuplicateFiles;
        set
        {
            if (TreeMapController.IsMetricDuplicateFiles == value)
                return;
            TreeMapController.IsMetricDuplicateFiles = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTreeMapMetricBytes));
            OnPropertyChanged(nameof(IsTreeMapMetricFiles));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateBytes));
        }
    }

    public bool IsTreeMapMetricDuplicateBytes
    {
        get => TreeMapController.IsMetricDuplicateBytes;
        set
        {
            if (TreeMapController.IsMetricDuplicateBytes == value)
                return;
            TreeMapController.IsMetricDuplicateBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTreeMapMetricBytes));
            OnPropertyChanged(nameof(IsTreeMapMetricFiles));
            OnPropertyChanged(nameof(IsTreeMapMetricDuplicateFiles));
        }
    }

    public object MaxDepth => TreeMapController.Options.MaxDepth + 2;

    public void LoadFromRepo()
    {
        using (TimingLog.StartPhase("LoadFromRepo()"))
        {
            var repoSnapshot = _repo.GetRepoSnapshotView();
            InitializeFromSnapshot(repoSnapshot);
        }
    }

    private void InitializeFromSnapshot(RepoSnapshotView snapshot)
    {
        ScanRootsTree.Rebuild(snapshot);

        DuplicateGroups.Rebuild(snapshot);

        using (TimingLog.StartPhase("BuildDirectoryTreeMap()"))
        {
            TreeMapController.Rebuild(snapshot);
        }
    }
}
