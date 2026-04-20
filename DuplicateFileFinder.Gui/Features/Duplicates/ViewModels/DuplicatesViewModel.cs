using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;

public class DuplicatesViewModel : ObservableObject
{
    private readonly IRepo _repo;
    private readonly IFileDirReadModel _fileDirIndex;
    private readonly DuplicateExplorerSelectionContext _selectionContext;

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly DisposableManager _disposer;

    public ScanRootsTreeViewModel ScanRootsTree { get; }
    public TreeMapActionsViewModel TreeMapActions { get; }
    public DuplicateGroupsViewModel DuplicateGroups { get; }

    public DuplicatesViewModel(
        IRepoHost host,
        ScanRootsTreeViewModel scanRootsTree,
        TreeMapController treeMapController,
        TreeMapActionsViewModel treeMapActions,
        DuplicateGroupsViewModel duplicateGroups,
        DuplicateExplorerSelectionContext selectionContext,
        DisposableManager disposer)
    {
        ArgumentNullException.ThrowIfNull(host);

        _repo = host.Repo;
        _fileDirIndex = host.FileDirIndex;
        _selectionContext = selectionContext ?? throw new ArgumentNullException(nameof(selectionContext));
        _disposer = disposer ?? throw new ArgumentNullException(nameof(disposer));

        ScanRootsTree = scanRootsTree ?? throw new ArgumentNullException(nameof(scanRootsTree));
        TreeMapController = treeMapController ?? throw new ArgumentNullException(nameof(treeMapController));
        TreeMapActions = treeMapActions ?? throw new ArgumentNullException(nameof(treeMapActions));
        DuplicateGroups = duplicateGroups ?? throw new ArgumentNullException(nameof(duplicateGroups));

        PropertyChangedEventHandler selectionHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(DuplicateExplorerSelectionContext.Current))
                SyncDuplicateGroupsFilterFromSelection();
        };
        _selectionContext.PropertyChanged += selectionHandler;
        _disposer.Add(() => _selectionContext.PropertyChanged -= selectionHandler);

        LoadFromRepo();
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
        var capturedSelection = _selectionContext.Current;

        ScanRootsTree.Rebuild(snapshot);

        DuplicateGroups.Rebuild(snapshot);

        using (TimingLog.StartPhase("BuildDirectoryTreeMap()"))
        {
            TreeMapController.Rebuild(snapshot);
        }

        var restoredSelection = ResolveSelectionTarget(snapshot, capturedSelection);
        _selectionContext.SetCurrent(restoredSelection, forceNotify: true);
    }

    private void SyncDuplicateGroupsFilterFromSelection()
    {
        var dirId = DuplicateSelectionTranslator.GetDesiredTreeDirectory(_selectionContext.Current);

        DirHandle? subtree = null;
        if (dirId is { } existingDirId && _fileDirIndex.TryGetDir(existingDirId, out var handle))
            subtree = handle;

        DuplicateGroups.SelectedSubtreeDir = subtree;
    }

    private DuplicateExplorerSelectionContext.SelectionTarget? ResolveSelectionTarget(
        RepoSnapshotView snapshot,
        DuplicateExplorerSelectionContext.SelectionTarget? captured)
    {
        if (captured is null)
            return null;

        if (captured.Value.Kind == DuplicateExplorerSelectionContext.SelectionKind.File
            && captured.Value.FileId is { } fileId
            && _fileDirIndex.TryGetFile(fileId, out _))
        {
            return captured;
        }

        DirId? desiredDirId = captured.Value.Kind switch
        {
            DuplicateExplorerSelectionContext.SelectionKind.Directory => captured.Value.DirId,
            DuplicateExplorerSelectionContext.SelectionKind.File => captured.Value.ParentDirId,
            DuplicateExplorerSelectionContext.SelectionKind.SyntheticDirectoryBucket => captured.Value.ParentDirId,
            _ => null
        };

        if (desiredDirId is not { } dirId)
            return null;

        if (!TryResolveExistingOrAncestorDirId(snapshot, dirId, out var resolvedDirId))
            return null;

        DirId? parentDirId = TryGetParentDirId(snapshot, resolvedDirId, out var parent)
            ? parent
            : null;

        return DuplicateExplorerSelectionContext.SelectionTarget.ForDirectory(resolvedDirId, parentDirId);
    }

    private bool TryResolveExistingOrAncestorDirId(
        RepoSnapshotView snapshot,
        DirId preferredDirId,
        out DirId resolvedDirId)
    {
        var current = preferredDirId;

        while (current >= 0)
        {
            if (_fileDirIndex.TryGetDir(current, out _))
            {
                resolvedDirId = current;
                return true;
            }

            if (!TryGetParentDirId(snapshot, current, out current))
                break;
        }

        resolvedDirId = -1;
        return false;
    }

    private bool TryGetParentDirId(
        RepoSnapshotView snapshot,
        DirId dirId,
        out DirId parentDirId)
    {
        parentDirId = -1;

        if (!_fileDirIndex.TryGetDir(dirId, out var handle))
            return false;

        var rec = snapshot.GetDirRecord(handle);
        if (rec.ParentDirId < 0)
            return false;

        parentDirId = rec.ParentDirId;
        return true;
    }
}
