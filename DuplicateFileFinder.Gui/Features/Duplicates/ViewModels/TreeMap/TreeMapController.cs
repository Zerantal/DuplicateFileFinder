using CommunityToolkit.Mvvm.ComponentModel;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.Domain;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public partial class TreeMapController : ObservableObject
{
    private readonly IRepo _repo;
    private readonly ITreeIndexReadModel _treeIndex;
    private readonly IFileDirReadModel _fileDirIndex;

    private RepoSnapshotView? _lastSnapshot;
    [ObservableProperty] private TreeMapMetric _metric = TreeMapMetric.TotalBytes;

    [ObservableProperty] private TreeMapNode<ITreeMapNodeElement>? _root;

    public TreeMapController(IRepoHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _repo = host.Repo;
        _treeIndex = host.TreeIndex;
        _fileDirIndex = host.FileDirIndex;
    }

    public TreeMapBuildOptions Options { get; init; } = TreeMapBuildOptions.Default;

    public bool IsMetricBytes
    {
        get => Metric == TreeMapMetric.TotalBytes;
        set
        {
            if (!value) return;
            if (Metric != TreeMapMetric.TotalBytes)
                Metric = TreeMapMetric.TotalBytes;
        }
    }

    public bool IsMetricFiles
    {
        get => Metric == TreeMapMetric.TotalFiles;
        set
        {
            if (!value) return;
            if (Metric != TreeMapMetric.TotalFiles)
                Metric = TreeMapMetric.TotalFiles;
        }
    }

    public void Rebuild(RepoSnapshotView snapshot)
    {
        _lastSnapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        Root = TreeMapBuilder.Build(
            snapshot,
            _repo.ScanRootsView,
            _treeIndex,
            _fileDirIndex,
            Metric,
            Options,
            (dirId) =>
            {
                _fileDirIndex.TryGetDirPathById(dirId, out var dirPath);
                return dirPath;
            });
    }

    partial void OnMetricChanged(TreeMapMetric value)
    {
        if (_lastSnapshot is null)
            return;

        Root = TreeMapBuilder.Build(
            _lastSnapshot,
            _repo.ScanRootsView,
            _treeIndex,
            _fileDirIndex,
            value,
            Options,
            (dirId) =>
            {
                _fileDirIndex.TryGetDirPathById(dirId, out var dirPath);
                return dirPath;
            });

        OnPropertyChanged(nameof(IsMetricBytes));
        OnPropertyChanged(nameof(IsMetricFiles));
    }
}