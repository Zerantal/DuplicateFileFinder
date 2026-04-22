using System.Collections.Immutable;
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

        using (_selectionContext.SuspendNotifications())
        {
            ScanRootsTree.Rebuild(snapshot);
            DuplicateGroups.Rebuild(snapshot);

            using (TimingLog.StartPhase("BuildDirectoryTreeMap()"))
            {
                TreeMapController.Rebuild(snapshot);
            }

            var restoredSelection = ResolveSelectionTarget(capturedSelection);
            _selectionContext.Current = restoredSelection;
        }
    }

    private void SyncDuplicateGroupsFilterFromSelection()
    {
        DirHandle? subtree = null;

        if (_selectionContext.Current?.ContextDirectoryId is { } dirId
            && _fileDirIndex.TryGetDir(dirId, out var handle))
        {
            subtree = handle;
        }

        DuplicateGroups.SelectedSubtreeDir = subtree;
    }

    private DuplicateExplorerSelectionContext.SelectionTarget? ResolveSelectionTarget(
        DuplicateExplorerSelectionContext.SelectionTarget? captured)
    {
        if (captured is null)
            return null;

        return captured.Value.Kind switch
        {
            DuplicateExplorerSelectionContext.SelectionKind.Directory =>
                ResolveDirectoryLikeSelection(captured.Value.DirectoryChain),

            DuplicateExplorerSelectionContext.SelectionKind.File =>
                ResolveFileSelection(captured.Value),

            DuplicateExplorerSelectionContext.SelectionKind.SyntheticDirectoryBucket =>
                ResolveDirectoryLikeSelection(captured.Value.DirectoryChain),

            _ => null
        };
    }

    private DuplicateExplorerSelectionContext.SelectionTarget? ResolveFileSelection(
        DuplicateExplorerSelectionContext.SelectionTarget captured)
    {
        if (captured.FileId is { } fileId && _fileDirIndex.TryGetFile(fileId, out _))
            return captured;

        return ResolveDirectoryLikeSelection(captured.DirectoryChain);
    }

    private DuplicateExplorerSelectionContext.SelectionTarget? ResolveDirectoryLikeSelection(
        ImmutableArray<DirId> capturedChain)
    {
        if (capturedChain.IsDefaultOrEmpty)
            return null;

        for (var i = capturedChain.Length - 1; i >= 0; i--)
        {
            var candidateDirId = capturedChain[i];
            if (!_fileDirIndex.TryGetDir(candidateDirId, out _))
                continue;

            var survivingChain = capturedChain.Take(i + 1).ToImmutableArray();
            return DuplicateExplorerSelectionContext.SelectionTarget.ForDirectory(survivingChain);
        }

        return null;
    }
}
