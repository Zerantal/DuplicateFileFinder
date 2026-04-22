using System.ComponentModel;
using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Application.Deletion;
using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

// ReSharper disable UnusedParameterInPartialMethod

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

public sealed partial class ScanRootsTreeViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DisposableManager _disposer;

    private readonly IScanRootsTreeNodeActions _actions;
    private readonly IDeletionWorkflowService _deletionService;
    private readonly ScanRootsTreeBuilder _builder;

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly SharedSelectionBinder<ScanRootsRowViewModel> _selectionBinder;

    // ScanRootId -> index in Rows where the depth-0 root row currently lives
    private readonly Dictionary<long, int> _rootIndexByScanRootId = new();

    // Remember expanded handles so Resort/Rebuild can restore expansion state
    private readonly HashSet<DirHandle> _expanded = [];

    // Handle->row for quick selection
    private readonly Dictionary<DirHandle, ScanRootsRowViewModel> _rowByHandle = new();

    private readonly DuplicateExplorerSelectionContext _selectionContext;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPath))]
    private ScanRootsRowViewModel? _selectedRow;

    private RepoSnapshotView? _snapshot;
    private readonly IFileDirReadModel _fileDirIndex;

    [ObservableProperty] private ScanRootsSortColumn _sortColumn = ScanRootsSortColumn.Size;

    [ObservableProperty] private bool _sortDescending = true;

    public ScanRootsTreeViewModel(
        IRepoHost host,
        RepoUiEventRelayPlugin repoEvents,
        ScanRootsTreeBuilder builder,
        IScanRootsTreeNodeActions actions,
        IDeletionWorkflowService deletionService,
        DisposableManager disposer,
        DuplicateExplorerSelectionContext selectionContext)
    {
        ArgumentNullException.ThrowIfNull(host);

        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _deletionService = deletionService ?? throw new ArgumentNullException(nameof(deletionService));
        _disposer = disposer ?? throw new ArgumentNullException(nameof(disposer));
        _selectionContext = selectionContext ?? throw new ArgumentNullException(nameof(selectionContext));
        _fileDirIndex = host.FileDirIndex;

        _selectionBinder = new SharedSelectionBinder<ScanRootsRowViewModel>(
            _selectionContext,
            getLocalSelection: () => SelectedRow,
            toSharedSelection: CreateSelectionTargetFromRow,
            applySharedSelection: target => ApplySelectionTarget(target, centerAfterSelect: true));

        SortByCommand = new RelayCommand<ScanRootsSortColumn>(SortBy);
        ToggleExpandedCommand = new RelayCommand<ScanRootsRowViewModel>(ToggleExpanded);

        repoEvents.ScanRootRemoved += ScanRootRemovedEventHandler;
        disposer.Add(() => repoEvents.ScanRootRemoved -= ScanRootRemovedEventHandler);

        PropertyChangedEventHandler selfHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedRow))
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

    private void ScanRootRemovedEventHandler(object? sender, RepoScanRootRemovedEvent e) =>
        RemoveScanRootFromRows(e.ScanRootIdValue);

    // Visible, virtualized rows
    public BulkObservableCollection<ScanRootsRowViewModel> Rows { get; } = [];

    public string? SelectedPath => SelectedRow?.FullPath;

    public ICommand SortByCommand { get; }
    public ICommand ToggleExpandedCommand { get; }

    public string NameArrow => ArrowFor(ScanRootsSortColumn.Name);
    public string SizeArrow => ArrowFor(ScanRootsSortColumn.Size);
    public string ItemsArrow => ArrowFor(ScanRootsSortColumn.Items);
    public string FilesArrow => ArrowFor(ScanRootsSortColumn.Files);
    public string DupFilesArrow => ArrowFor(ScanRootsSortColumn.DupFiles);
    public string DupBytesArrow => ArrowFor(ScanRootsSortColumn.DupBytes);

    public event Action? RequestCenterSelectedRow;

    // ReSharper disable once MemberCanBePrivate.Global
    public void SelectRowAndCenter(ScanRootsRowViewModel row)
    {
        SelectedRow = row;
        RequestCenterSelectedRow?.Invoke();
    }

    public void Rebuild(RepoSnapshotView snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        var expandedToRestore = _expanded.ToArray();

        BuildFromRoots();
        RestoreExpandedState(expandedToRestore);
    }

    private void BuildFromRoots()
    {
        if (_snapshot is null)
            return;

        var rootModels = _builder.Build(_snapshot);

        Rows.BeginUpdate();
        try
        {
            Rows.Clear();
            _rowByHandle.Clear();
            _rootIndexByScanRootId.Clear();

            foreach (var model in rootModels)
                Rows.Add(CreateRow(model, 0));

            ApplyRootSort();
            RebuildRootIndexMap();
        }
        finally
        {
            Rows.EndUpdate();
        }
    }

    private void RebuildRootIndexMap()
    {
        _rootIndexByScanRootId.Clear();

        for (var i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i];
            if (r.Depth != 0)
                break; // depth-0 block should be at the front

            _rootIndexByScanRootId[r.ScanRootId] = i;
        }
    }


    private ScanRootsRowViewModel CreateRow(ScanRootsTreeNode model, int depth)
    {
        var row = new ScanRootsRowViewModel(model, _actions, _deletionService, depth);

        if (model.Dir.IsValid)
            _rowByHandle[model.Dir] = row;

        if (depth == 0)
        {
            // optimistic removal to avoid races with user actions while repo work completes
            row.OnRootRemoved = _ => RemoveScanRootFromRows(model.ScanRootId);
        }

        return row;
    }

    private void SortBy(ScanRootsSortColumn column)
    {
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = true;
        }

        ResortVisible();
        OnSortHeaderChanged();
    }

    // ---- Sort indicators

    private string ArrowFor(ScanRootsSortColumn column)
        => SortColumn != column ? string.Empty : SortDescending ? " ▼" : " ▲";

    partial void OnSortColumnChanged(ScanRootsSortColumn value) => OnSortHeaderChanged();
    partial void OnSortDescendingChanged(bool value) => OnSortHeaderChanged();

    private void OnSortHeaderChanged()
    {
        OnPropertyChanged(nameof(NameArrow));
        OnPropertyChanged(nameof(SizeArrow));
        OnPropertyChanged(nameof(ItemsArrow));
        OnPropertyChanged(nameof(FilesArrow));
        OnPropertyChanged(nameof(DupFilesArrow));
        OnPropertyChanged(nameof(DupBytesArrow));
    }

    // ---- Expand/collapse ----

    private void ToggleExpanded(ScanRootsRowViewModel? row)
    {
        if (row is null)
            return;

        if (!row.HasLazyChildren)
            return;

        if (row.IsExpanded)
            Collapse(row);
        else
            Expand(row);
    }

    private void Expand(ScanRootsRowViewModel row)
    {
        if (_snapshot is null)
            return;

        if (row.IsExpanded)
            return;

        // Materialize children MODELS for the row (may be heavy for huge fan-out)
        // This is for user-driven expansion. Treemap navigation uses a fast path below. :contentReference[oaicite:8]{index=8}
        _builder.EnsureChildrenLoaded(row.Model);

        var insertAt = Rows.IndexOf(row);
        if (insertAt < 0)
            return;

        var childDepth = row.Depth + 1;

        var children = row.Model.Children;
        var ordered = OrderModels(children);

        Rows.BeginUpdate();
        try
        {
            row.IsExpanded = true;
            _expanded.Add(row.Dir);

            var idx = insertAt + 1;
            foreach (var child in ordered)
                Rows.Insert(idx++, CreateRow(child, childDepth));

            var inserted = ordered.Count;
            AdjustRootIndicesAfterMutation(startIndexInclusive: insertAt + 1, delta: inserted);
        }
        finally
        {
            Rows.EndUpdate();
        }
    }

    private void Collapse(ScanRootsRowViewModel row)
    {
        if (!row.IsExpanded)
            return;

        var start = Rows.IndexOf(row);
        if (start < 0)
            return;

        Rows.BeginUpdate();
        try
        {
            row.IsExpanded = false;
            _expanded.Remove(row.Dir);

            var parentDepth = row.Depth;
            var i = start + 1;

            var removed = 0;
            while (i < Rows.Count && Rows[i].Depth > parentDepth)
            {
                var r = Rows[i];
                if (r.Dir.IsValid)
                {
                    _expanded.Remove(r.Dir);
                    _rowByHandle.Remove(r.Dir);
                }

                Rows.RemoveAt(i);
                removed++;
            }

            if (removed > 0)
                AdjustRootIndicesAfterMutation(startIndexInclusive: start + 1, delta: -removed);
        }
        finally
        {
            Rows.EndUpdate();
        }
    }

    // ---- Navigation API

    public void NavigateToDir(DirHandle dirHandle)
    {
        var row = TryEnsureVisibleRow(dirHandle);
        if (row is not null)
            SelectRowAndCenter(row);
    }

    public void NavigateToFile(FileHandle fileHandle)
    {
        if (_builder.TryGetParentDirHandle(fileHandle, out var parent))
            NavigateToDir(parent);
    }

    // ---- Ordering ----

    private List<ScanRootsTreeNode> OrderModels(List<ScanRootsTreeNode> models)
    {
        var list = models.ToList();
        list.Sort(GetModelComparison());

        if (SortDescending)
            list.Reverse();

        return list;
    }

    private void ApplyRootSort()
    {
        if (Rows.Count <= 1)
            return;

        // Only sort depth 0 rows (roots) on rebuild; expansion inserts are sorted per-parent.
        var rootCount = 0;
        while (rootCount < Rows.Count && Rows[rootCount].Depth == 0)
            rootCount++;

        var roots = Rows.Take(rootCount).ToList();
        roots.Sort((a, b) => GetComparison()(a, b));
        if (SortDescending)
            roots.Reverse();

        Rows.BeginUpdate();
        try
        {
            for (var i = 0; i < rootCount; i++)
                Rows[i] = roots[i];
        }
        finally
        {
            Rows.EndUpdate();
        }
    }


    private void ResortVisible()
    {
        // Minimal-pain approach:
        // rebuild from roots, then re-apply expansions from _expanded.
        if (_snapshot is null)
            return;

        var toRestoreExpanded = _expanded.ToArray();

        BuildFromRoots();
        RestoreExpandedState(toRestoreExpanded);
        ApplySelectionTarget(_selectionContext.Current);
    }

    private Comparison<ScanRootsTreeNode> GetModelComparison()
    {
        return SortColumn switch
        {
            ScanRootsSortColumn.Name => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            ScanRootsSortColumn.Size => static (a, b) => a.TotalBytes.CompareTo(b.TotalBytes),
            ScanRootsSortColumn.Items => static (a, b) => a.ItemCount.CompareTo(b.ItemCount),
            ScanRootsSortColumn.Files => static (a, b) => a.FileCount.CompareTo(b.FileCount),
            ScanRootsSortColumn.DupFiles => static (a, b) => a.DuplicateFiles.CompareTo(b.DuplicateFiles),
            ScanRootsSortColumn.DupBytes => static (a, b) => a.DuplicateBytes.CompareTo(b.DuplicateBytes),
            _ => static (_, _) => 0
        };
    }

    private Comparison<ScanRootsRowViewModel> GetComparison()
    {
        return SortColumn switch
        {
            ScanRootsSortColumn.Name => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            ScanRootsSortColumn.Size => static (a, b) => a.TotalBytes.CompareTo(b.TotalBytes),
            ScanRootsSortColumn.Items => static (a, b) => a.ItemCount.CompareTo(b.ItemCount),
            ScanRootsSortColumn.Files => static (a, b) => a.FileCount.CompareTo(b.FileCount),
            ScanRootsSortColumn.DupFiles => static (a, b) => a.DuplicateFiles.CompareTo(b.DuplicateFiles),
            ScanRootsSortColumn.DupBytes => static (a, b) => a.DuplicateBytes.CompareTo(b.DuplicateBytes),
            _ => static (_, _) => 0
        };
    }

    private void RemoveScanRootFromRows(long scanRootId)
    {
        if (!TryGetValidatedRootIndex(scanRootId, out var rootIndex))
            return;

        var endExclusive = rootIndex + 1;
        while (endExclusive < Rows.Count && Rows[endExclusive].Depth > 0)
            endExclusive++;

        var rowsToRemove = endExclusive - rootIndex;
        if (rowsToRemove <= 0)
            return;

        ClearSelectionIfInsideRemovedRoot(rootIndex, rowsToRemove);

        Rows.BeginUpdate();
        try
        {
            for (var i = 0; i < rowsToRemove; i++)
            {
                var row = Rows[rootIndex];
                if (row.Dir.IsValid)
                {
                    _expanded.Remove(row.Dir);
                    _rowByHandle.Remove(row.Dir);
                }

                Rows.RemoveAt(rootIndex);
            }

            _rootIndexByScanRootId.Remove(scanRootId);
            AdjustRootIndicesAfterMutation(rootIndex, -rowsToRemove);
        }
        finally
        {
            Rows.EndUpdate();
        }
    }

    private void AdjustRootIndicesAfterMutation(int startIndexInclusive, int delta)
    {
        var keys = _rootIndexByScanRootId.Keys.ToArray();
        foreach (var key in keys)
        {
            var idx = _rootIndexByScanRootId[key];
            if (idx >= startIndexInclusive)
                _rootIndexByScanRootId[key] = idx + delta;
        }
    }

    private bool TryGetValidatedRootIndex(long scanRootId, out int rootIndex)
    {
        if (!_rootIndexByScanRootId.TryGetValue(scanRootId, out rootIndex))
            return false;

        if (rootIndex >= 0 && rootIndex < Rows.Count)
            return true;

        RebuildRootIndexMap();

        return _rootIndexByScanRootId.TryGetValue(scanRootId, out rootIndex)
               && rootIndex >= 0
               && rootIndex < Rows.Count;
    }

    private void ClearSelectionIfInsideRemovedRoot(int rootIndex, int rowsToRemove)
    {
        if (SelectedRow is null)
            return;

        var selectedIndex = Rows.IndexOf(SelectedRow);
        if (selectedIndex >= rootIndex && selectedIndex < rootIndex + rowsToRemove)
            SelectedRow = null;
    }

    // ----------------------------
    // Selection synchronisation
    // ----------------------------

    private DuplicateExplorerSelectionContext.SelectionTarget? CreateSelectionTargetFromRow(
        ScanRootsRowViewModel? row)
    {
        if (_snapshot is null)
            return null;

        return DuplicateSelectionTranslator.FromTreeRow(_snapshot, _fileDirIndex, row);
    }

    private bool TryResolveExistingOrAncestorHandle(DirHandle preferred, out DirHandle resolved)
    {
        var current = preferred;

        while (current.IsValid)
        {
            if (_rowByHandle.ContainsKey(current))
            {
                resolved = current;
                return true;
            }

            if (!_builder.TryGetParentDirHandle(current, out current))
                break;
        }

        resolved = DirHandle.Invalid;
        return false;
    }

    private void ApplySelectionTarget(
        DuplicateExplorerSelectionContext.SelectionTarget? target,
        bool centerAfterSelect = false)
    {
        var handle = ResolveHandleFromSelectionTarget(target);
        if (handle is not { IsValid: true } dirHandle)
        {
            SelectedRow = null;
            return;
        }

        if (!EnsureVisible(dirHandle))
        {
            if (!TryResolveExistingOrAncestorHandle(dirHandle, out var resolvedAncestor) ||
                !EnsureVisible(resolvedAncestor))
            {
                SelectedRow = null;
                return;
            }

            dirHandle = resolvedAncestor;
        }

        SelectedRow = _rowByHandle.GetValueOrDefault(dirHandle);

        if (centerAfterSelect && SelectedRow is not null)
            RequestCenterSelectedRow?.Invoke();
    }

    private DirHandle? ResolveHandleFromSelectionTarget(
        DuplicateExplorerSelectionContext.SelectionTarget? target)
    {
        if (_snapshot is null)
            return null;

        if (target?.ContextDirectoryId is not { } desiredDirId)
            return null;

        return _builder.TryGetDirHandle(desiredDirId, out var handle)
            ? handle
            : null;
    }

    private void RestoreExpandedState(IEnumerable<DirHandle> expandedHandles)
    {
        _expanded.Clear();

        if (_snapshot is null)
            return;

        var ordered = expandedHandles
            .Select(h =>
            {
                var depth = _builder.TryBuildAncestorChainToScanRoot(h, out var chain)
                    ? chain.Count
                    : int.MaxValue;
                return (Handle: h, Depth: depth);
            })
            .OrderBy(x => x.Depth)
            .ToArray();

        foreach (var item in ordered)
        {
            if (item.Depth == int.MaxValue)
                continue;

            if (!_builder.TryBuildAncestorChainToScanRoot(item.Handle, out var chain) || chain.Count == 0)
                continue;

            if (!_rowByHandle.TryGetValue(chain[0], out var current))
                continue;

            for (var i = 1; i < chain.Count; i++)
            {
                if (current is { IsExpanded: false, HasLazyChildren: true })
                    Expand(current);

                if (!_rowByHandle.TryGetValue(chain[i], out current))
                {
                    current = null!;
                    break;
                }
            }

            if (current is { HasLazyChildren: true, IsExpanded: false })
                Expand(current);
        }
    }

    private bool EnsureVisible(DirHandle handle) => TryEnsureVisibleRow(handle) is not null;

    private ScanRootsRowViewModel? TryEnsureVisibleRow(DirHandle target)
    {
        if (_snapshot is null)
            return null;

        if (_rowByHandle.TryGetValue(target, out var existing))
            return existing;

        if (!_builder.TryBuildAncestorChainToScanRoot(target, out var chain) || chain.Count == 0)
            return null;

        if (!_rowByHandle.TryGetValue(chain[0], out var current))
            return null;

        for (var i = 1; i < chain.Count; i++)
        {
            if (!current.IsExpanded)
                Expand(current);

            if (!_rowByHandle.TryGetValue(chain[i], out current))
                return null;
        }

        return current;
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
