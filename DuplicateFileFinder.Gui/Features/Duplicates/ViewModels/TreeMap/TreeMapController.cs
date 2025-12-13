using CommunityToolkit.Mvvm.ComponentModel;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.Domain;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public partial class TreeMapController : ObservableObject
{
    private readonly IRepo _repo;
    private readonly ITreeIndexReadModel _treeIndex;

    private IRepoView? _lastSnapshot;
    [ObservableProperty] private TreeMapMetric _metric = TreeMapMetric.TotalBytes;

    [ObservableProperty] private TreeMapNode<ITreeMapNodeElement>? _root;

    public TreeMapController(IRepo repo, ITreeIndexReadModel treeIndex)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _treeIndex = treeIndex ?? throw new ArgumentNullException(nameof(treeIndex));
    }

    public TreeMapBuildOptions Options { get; } = TreeMapBuildOptions.Default;

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

    public void Rebuild(IRepoView snapshot)
    {
        _lastSnapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        Root = TreeMapBuilder.Build(
            snapshot,
            _repo.ScanRootsView,
            _treeIndex,
            Metric,
            Options,
            (dirId) => _repo.GetDirPath(dirId, true));
    }

    partial void OnMetricChanged(TreeMapMetric value)
    {
        if (_lastSnapshot is null)
            return;

        Root = TreeMapBuilder.Build(
            _lastSnapshot,
            _repo.ScanRootsView,
            _treeIndex,
            value,
            Options,
            (dirId) =>  _repo.GetDirPath(dirId, true));

        OnPropertyChanged(nameof(IsMetricBytes));
        OnPropertyChanged(nameof(IsMetricFiles));
    }
}