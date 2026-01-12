// ViewModels/DuplicatesViewModel.cs

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Domain;
using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;

public partial class DuplicatesViewModel : ObservableObject
{
    private readonly DuplicatesController _controller;
    private readonly IRepo _repo;
    private readonly TreeMapController _treeMap;

    private readonly IFileDirReadModel _fileDirIndex;
    private readonly IDialogService _dialogs;
    private readonly IFileSystemDeleteService _deleter;


    public ScanRootsTreeViewModel ScanRootsTree { get; }
    public TreeMapActionsViewModel TreeMapActions { get; }
    public DuplicateGroups.DuplicateGroupsViewModel DuplicateGroups { get; }

    public DuplicatesViewModel(
        IRepoHost host,
        IScanCoordinator scanner,
        IDialogService dialogService,
        IFileSystemDeleteService deleter)
    {
        ArgumentNullException.ThrowIfNull(host);

        _fileDirIndex = host.FileDirIndex;
        _dialogs = dialogService;
        _deleter = deleter;
        _repo = host.Repo;
        var hashIndexService = host.HashIndex;

        // Duplicate groups view
        DuplicateGroups = new DuplicateGroups.DuplicateGroupsViewModel(host, dialogService, deleter);

        // folder view
        var treeBuilder = new ScanRootsTreeBuilder(host, scanner, dialogService, deleter);
        ScanRootsTree = new ScanRootsTreeViewModel(treeBuilder);
        ScanRootsTree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScanRootsTreeViewModel.SelectedPath))
                DuplicateGroups.SelectedFolderPrefix = ScanRootsTree.SelectedPath;
        };

        // Treemap
        _treeMap = new TreeMapController(host) { Options = new TreeMapBuildOptions { MaxDepth = 8 } };
        _treeMap.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TreeMapController.SelectedNode))
                OnTreeMapSelectionChanged();
        };

        TreeMapActions = new TreeMapActionsViewModel(host, scanner, dialogService, deleter);

        _controller = new DuplicatesController(host, hashIndexService);

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

    public BulkObservableCollection<DuplicateSetRow> FilteredSets => _controller.FilteredSets;

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

        OnPropertyChanged(nameof(FilteredSets));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedDuplicateFileCommand))]
    private FileItem? _selectedDuplicateFile;

    private bool CanDeleteSelectedDuplicateFile() => SelectedDuplicateFile is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedDuplicateFile))]
    private Task DeleteSelectedDuplicateFileAsync()
        => DeleteDuplicateFileAsync(SelectedDuplicateFile);

    private async Task DeleteDuplicateFileAsync(FileItem? item)
    {
        if (item is null)
            return;

        var fullPath = item.Value.FullPath;
        if (string.IsNullOrWhiteSpace(fullPath))
            return;

        // Confirm
        var ok = await _dialogs.ShowConfirmationAsync(
            title: "Delete file",
            message: $"Delete this file from disk?\n\n{fullPath}",
            okText: "Delete",
            cancelText: "Cancel");

        if (!ok)
            return;

        // Delete from disk first
        var (deleted, deleteErr) = await _deleter.DeleteFileAsync(fullPath);
        if (!deleted)
        {
            await _dialogs.ShowErrorAsync(
                title: "Delete failed",
                message: deleteErr ?? "Unknown error.");
            return;
        }

        // Now delete from repo using an opaque handle resolved via the index.
        if (!_fileDirIndex.TryGetFile(item.Value.Id, out var fileHandle))
        {
            await _dialogs.ShowErrorAsync(
                title: "Delete error",
                message: "Deleted file from disk, but could not resolve the file handle in the index. " +
                         "The repository may still show the file until the next rescan/rebuild.");
            return;
        }

        var repoResult = await _repo.DeleteFileAsync(fileHandle);
        if (!repoResult.Success)
        {
            await _dialogs.ShowErrorAsync(
                title: "Delete error",
                message: $"Deleted file from disk, but deleting entry from repository failed: {repoResult.Error}");
            return;
        }

        if (Equals(SelectedDuplicateFile, item))
            SelectedDuplicateFile = null;

        _controller.SelectedSet?.TryRemoveItemByFileId(item.Value.Id);
    }
}
