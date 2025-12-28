// ViewModels/DuplicatesViewModel.cs

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.Domain;
using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.Duplicates;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;
using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateSetRow = DuplicateFileFinder.Gui.Features.Duplicates.Models.DuplicateSetRow;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;

public partial class DuplicatesViewModel : ObservableObject
{
    private readonly DuplicatesController _duplicates;

    private readonly FolderTreeBuilder _folderTreeBuilder;
    private readonly IRepo _repo;
    private readonly TreeMapController _treeMap;

    public DuplicatesViewModel(IRepoHost host, IScanCoordinator scanner)
    {
        ArgumentNullException.ThrowIfNull(host);

        _repo = host.Repo;
        var hashIndexService = host.HashIndex;

        _folderTreeBuilder = new FolderTreeBuilder(host, scanner);
        _treeMap = new TreeMapController(host)
        {
            Options = new TreeMapBuildOptions
            {
                MaxDepth = 8
            }
        };
        
        
        _treeMap.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TreeMapController.Root))
                OnPropertyChanged(nameof(DirectoryTreeMapRoot));
        };

        _duplicates = new DuplicatesController(host, hashIndexService);
        _duplicates.PropertyChanged += (_, e) =>
        {
            // bubble up for existing bindings (if your view binds directly to VM props)
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
    public ObservableCollection<FolderNodeViewModel> FolderRoots { get; } = [];

    // Expose treemap for binding
    public TreeMapNode<ITreeMapNodeElement>? DirectoryTreeMapRoot => _treeMap.Root;

    public bool IsTreeMapMetricBytes
    {
        get => _treeMap.IsMetricBytes;
        set
        {
            if (_treeMap.IsMetricBytes == value) return;
            _treeMap.IsMetricBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTreeMapMetricFiles));
        }
    }

    public bool IsTreeMapMetricFiles
    {
        get => _treeMap.IsMetricFiles;
        set
        {
            if (_treeMap.IsMetricFiles == value) return;
            _treeMap.IsMetricFiles = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTreeMapMetricBytes));
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
        FolderRoots.Clear();

        using (TimingLog.StartPhase("BuildFolderTree()"))
        {
            _folderTreeBuilder.Rebuild(snapshot, FolderRoots);
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
        OnPropertyChanged(nameof(DirectoryTreeMapRoot));
    }
}