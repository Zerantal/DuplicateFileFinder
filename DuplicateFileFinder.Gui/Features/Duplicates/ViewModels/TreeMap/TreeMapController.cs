using CommunityToolkit.Mvvm.ComponentModel;

using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.Application.TreeMap;

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

    // Fast lookup tables for selection sync (flat tree -> treemap)
    private readonly Dictionary<DirHandle, TreeMapNode<ITreeMapNodeElement>> _dirNodeByHandle = new();
    private readonly Dictionary<FileHandle, TreeMapNode<ITreeMapNodeElement>> _fileNodeByHandle = new();

    public IReadOnlyDictionary<DirHandle, TreeMapNode<ITreeMapNodeElement>> DirNodeByHandle => _dirNodeByHandle;
    public IReadOnlyDictionary<FileHandle, TreeMapNode<ITreeMapNodeElement>> FileNodeByHandle => _fileNodeByHandle;

    [ObservableProperty] private TreeMapMetric _metric = TreeMapMetric.TotalBytes;
    [ObservableProperty] private TreeMapNode<ITreeMapNodeElement>? _root;

    [ObservableProperty] private TreeMapNode<ITreeMapNodeElement>? _selectedNode;

    public TreeMapController(IRepoHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _repo = host.Repo ?? throw new ArgumentNullException(nameof(host));
        _treeIndex = host.TreeIndex;
        _fileDirIndex = host.FileDirIndex;
    }

    public TreeMapBuildOptions Options { get; init; } = TreeMapBuildOptions.Default;

    public bool IsMetricBytes
    {
        get => Metric == TreeMapMetric.TotalBytes;
        set
        {
            if (!value)
                return;
            if (Metric != TreeMapMetric.TotalBytes)
                Metric = TreeMapMetric.TotalBytes;
        }
    }

    public bool IsMetricFiles
    {
        get => Metric == TreeMapMetric.TotalFiles;
        set
        {
            if (!value)
                return;
            if (Metric != TreeMapMetric.TotalFiles)
                Metric = TreeMapMetric.TotalFiles;
        }
    }

    public bool IsMetricDuplicateFiles
    {
        get => Metric == TreeMapMetric.DuplicateFiles;
        set
        {
            if (!value)
                return;
            if (Metric != TreeMapMetric.DuplicateFiles)
                Metric = TreeMapMetric.DuplicateFiles;
        }
    }

    public bool IsMetricDuplicateBytes
    {
        get => Metric == TreeMapMetric.DuplicateBytes;
        set
        {
            if (!value)
                return;
            if (Metric != TreeMapMetric.DuplicateBytes)
                Metric = TreeMapMetric.DuplicateBytes;
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
            dirId =>
            {
                _fileDirIndex.TryGetDirPathById(dirId, out var dirPath);
                return dirPath;
            });

        RebuildLookups();
        SelectedNode = null;
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
            dirId =>
            {
                _fileDirIndex.TryGetDirPathById(dirId, out var dirPath);
                return dirPath;
            });

        RebuildLookups();
        SelectedNode = null;

        OnPropertyChanged(nameof(IsMetricBytes));
        OnPropertyChanged(nameof(IsMetricFiles));
        OnPropertyChanged(nameof(IsMetricDuplicateFiles));
        OnPropertyChanged(nameof(IsMetricDuplicateBytes));
    }

    private void RebuildLookups()
    {
        _dirNodeByHandle.Clear();
        _fileNodeByHandle.Clear();

        if (Root is null)
            return;

        var stack = new Stack<TreeMapNode<ITreeMapNodeElement>>();
        stack.Push(Root);

        while (stack.Count > 0)
        {
            var n = stack.Pop();

            if (n.Element is DirTreeMapElement d)
                _dirNodeByHandle[d.Dir] = n;
            else if (n.Element is FileTreeMapElement f)
                _fileNodeByHandle[f.File] = n;

            if (n.Children is { Count: > 0 })
            {
                for (var i = 0; i < n.Children.Count; i++)
                    stack.Push(n.Children[i]);
            }
        }
    }
}
