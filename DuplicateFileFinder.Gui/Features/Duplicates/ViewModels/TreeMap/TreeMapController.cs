using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.Application.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public partial class TreeMapController : ObservableObject, IAsyncDisposable
{
    private readonly IRepo _repo;
    private readonly ITreeIndexReadModel _treeIndex;
    private readonly IFileDirReadModel _fileDirIndex;
    private readonly DisposableManager _disposer;

    private readonly DuplicateExplorerSelectionContext _selectionContext;

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly SharedSelectionBinder<TreeMapNode<ITreeMapNodeElement>> _selectionBinder;

    private RepoSnapshotView? _lastSnapshot;

    // Fast lookup tables for selection sync (flat tree -> treemap)
    private readonly Dictionary<DirHandle, TreeMapNode<ITreeMapNodeElement>> _dirNodeByHandle = new();
    private readonly Dictionary<FileHandle, TreeMapNode<ITreeMapNodeElement>> _fileNodeByHandle = new();

    public IReadOnlyDictionary<DirHandle, TreeMapNode<ITreeMapNodeElement>> DirNodeByHandle => _dirNodeByHandle;
    public IReadOnlyDictionary<FileHandle, TreeMapNode<ITreeMapNodeElement>> FileNodeByHandle => _fileNodeByHandle;

    [ObservableProperty] private TreeMapMetric _metric = TreeMapMetric.TotalBytes;
    [ObservableProperty] private TreeMapNode<ITreeMapNodeElement>? _root;

    [ObservableProperty] private TreeMapNode<ITreeMapNodeElement>? _selectedNode;

    public TreeMapController(IRepoHost host,
        DuplicateExplorerSelectionContext selectionContext,
        DisposableManager disposer)
    {
        ArgumentNullException.ThrowIfNull(host);
        _selectionContext = selectionContext ?? throw new ArgumentNullException(nameof(selectionContext));
        _disposer = disposer ?? throw new ArgumentNullException(nameof(disposer));

        _repo = host.Repo ?? throw new ArgumentNullException(nameof(host));
        _treeIndex = host.TreeIndex;
        _fileDirIndex = host.FileDirIndex;

        _selectionBinder = new SharedSelectionBinder<TreeMapNode<ITreeMapNodeElement>>(
            _selectionContext,
            getLocalSelection: () => SelectedNode,
            toSharedSelection: CreateSelectionTargetFromNode,
            applySharedSelection: ApplySelectionTarget);


        PropertyChangedEventHandler selfHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedNode))
                _selectionBinder.PublishFromLocal();
        };

        PropertyChangedEventHandler selectionHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(DuplicateExplorerSelectionContext.Current))
                _selectionBinder.ApplyFromShared();
        };
        PropertyChanged += selfHandler;
        _selectionContext.PropertyChanged += selectionHandler;

        _disposer.Add(() => PropertyChanged -= selfHandler);
        _disposer.Add(() => _selectionContext.PropertyChanged -= selectionHandler);
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
        ApplySelectionTarget(_selectionContext.Current);
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
        ApplySelectionTarget(_selectionContext.Current);

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

    // ------------------------------
    // Selection synchronisation
    // ------------------------------

    private DuplicateExplorerSelectionContext.SelectionTarget? CreateSelectionTargetFromNode(
        TreeMapNode<ITreeMapNodeElement>? node)
    {
        if (_lastSnapshot is null)
            return null;

        return DuplicateSelectionTranslator.FromTreeMapNode(_lastSnapshot, _fileDirIndex, node);
    }

    private void ApplySelectionTarget(DuplicateExplorerSelectionContext.SelectionTarget? target) =>
        SelectedNode = ResolveNodeFromSelectionTarget(target);

    private TreeMapNode<ITreeMapNodeElement>? ResolveNodeFromSelectionTarget(
        DuplicateExplorerSelectionContext.SelectionTarget? target)
    {
        if (target is null)
            return null;

        if (target.Value.Kind == DuplicateExplorerSelectionContext.SelectionKind.File
            && target.Value.FileId is { } fileId
            && _fileDirIndex.TryGetFile(fileId, out var fileHandle)
            && _fileNodeByHandle.TryGetValue(fileHandle, out var fileNode))
        {
            return fileNode;
        }

        if (target.Value.ContextDirectoryId is { } dirId
            && TryResolveExistingOrAncestorDirNode(dirId, out var dirNode))
        {
            return dirNode;
        }

        return null;
    }

    private bool TryResolveExistingOrAncestorDirNode(
        DirId preferredDirId,
        out TreeMapNode<ITreeMapNodeElement> node)
    {
        var current = preferredDirId;

        while (current >= 0)
        {
            if (_fileDirIndex.TryGetDir(current, out var handle)
                && _dirNodeByHandle.TryGetValue(handle, out var resolvedNode))
            {
                node = resolvedNode;
                return true;
            }

            if (!TryGetParentDirId(current, out current))
                break;
        }

        node = null!;
        return false;
    }

    private bool TryGetParentDirId(DirId dirId, out DirId parentDirId)
    {
        parentDirId = -1;

        if (_lastSnapshot is null)
            return false;

        if (!_fileDirIndex.TryGetDir(dirId, out var handle))
            return false;

        var rec = _lastSnapshot.GetDirRecord(handle);
        if (rec.ParentDirId < 0)
            return false;

        parentDirId = rec.ParentDirId;
        return true;
    }

    // ------------------------------
    // Cleanup
    // ------------------------------
    public ValueTask DisposeAsync()
    {
        _disposer.Dispose();
        return ValueTask.CompletedTask;
    }
}
