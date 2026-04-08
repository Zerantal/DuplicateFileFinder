using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Application.Deletion;
using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Core.RepoEventing;

// ReSharper disable UnusedParameterInPartialMethod

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

public sealed partial class ScanRootsTreeViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DisposableManager _disposer;

    private readonly IScanRootsTreeNodeActions _actions;
    private readonly IDeletionWorkflowService _deletionService;
    private readonly ScanRootsTreeBuilder _builder;

    // ScanRootId -> index in Rows where the depth-0 root row currently lives
    private readonly Dictionary<long, int> _rootIndexByScanRootId = new();

    // Remember expanded handles so Resort/Rebuild can restore expansion state
    private readonly HashSet<DirHandle> _expanded = new();

    // Handle->row for quick selection
    private readonly Dictionary<DirHandle, ScanRootsRowViewModel> _rowByHandle = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPath))]
    private ScanRootsRowViewModel? _selectedRow;

    private RepoSnapshotView? _snapshot;

    [ObservableProperty] private ScanRootsSortColumn _sortColumn = ScanRootsSortColumn.Size;

    [ObservableProperty] private bool _sortDescending = true;

    public ScanRootsTreeViewModel(
        RepoUiEventRelayPlugin repoEvents,
        ScanRootsTreeBuilder builder,
        IScanRootsTreeNodeActions actions,
        IDeletionWorkflowService deletionService,
        DisposableManager disposer)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _deletionService = deletionService ?? throw new ArgumentNullException(nameof(deletionService));
        _disposer = disposer ?? throw new ArgumentNullException(nameof(disposer));

        SortByCommand = new RelayCommand<ScanRootsSortColumn>(SortBy);
        ToggleExpandedCommand = new RelayCommand<ScanRootsRowViewModel>(ToggleExpanded);

        repoEvents.ScanRootRemoved += ScanRootRemovedEventHandler;
        disposer.Add(() => repoEvents.ScanRootRemoved -= ScanRootRemovedEventHandler);
    }

    private void ScanRootRemovedEventHandler(object? sender, RepoScanRootRemovedEvent e)
    {
        RemoveScanRootFromRows(e.ScanRootId);
    }

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
        _expanded.Clear();
        BuildFromRoots();
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
            RebuildRootIndexMap_NoLock();
        }
        finally
        {
            Rows.EndUpdate();
        }
    }

    private void RebuildRootIndexMap_NoLock()
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
            AdjustRootIndicesAfterMutation_NoLock(startIndexInclusive: insertAt + 1, delta: inserted);
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
                AdjustRootIndicesAfterMutation_NoLock(startIndexInclusive: start + 1, delta: -removed);
        }
        finally
        {
            Rows.EndUpdate();
        }
    }

    // ---- Navigation API

    // public void NavigateToDir(DirHandle dirHandle) => NavigateToDirHandleLazyFast(dirHandle);
    public void NavigateToDir(DirHandle dirHandle) => NavigateToDirHandleLazyFull(dirHandle);

    public void NavigateToFile(FileHandle fileHandle)
    {
        // if (_builder.TryGetParentDirHandle(fileHandle, out var parent))
        //     NavigateToDirHandleLazyFast(parent);
        if (_builder.TryGetParentDirHandle(fileHandle, out var parent))
            NavigateToDirHandleLazyFull(parent);
    }

    // This expands the path using full child creation, which materializes all siblings.
    private void NavigateToDirHandleLazyFull(DirHandle target)
    {
        if (_snapshot is null)
            return;

        if (!_builder.TryBuildAncestorChainToScanRoot(target, out var chain))
            return;

        // Find root row
        if (!_rowByHandle.TryGetValue(chain[0], out var current))
            return;

        // Expand path with full sibling enumeration
        for (var i = 1; i < chain.Count; i++)
        {
            var parentRow = current;
            var parentIndex = Rows.IndexOf(parentRow);
            if (parentIndex < 0)
                return;

            if (!parentRow.IsExpanded)
                Expand(parentRow);

            var childHandle = chain[i];

            if (!_rowByHandle.TryGetValue(childHandle, out current))
                return;
        }

        SelectRowAndCenter(current);
    }

    // This only expands the path using fast child creation, without loading all siblings
    // ReSharper disable once UnusedMember.Local
    private void NavigateToDirHandleLazyFast(DirHandle target)
    {
        if (_snapshot is null)
            return;

        if (!_builder.TryBuildAncestorChainToScanRoot(target, out var chain))
            return;

        // Find root row
        if (!_rowByHandle.TryGetValue(chain[0], out var current))
            return;

        // Expand path using FAST child creation (no sibling enumeration)
        for (var i = 1; i < chain.Count; i++)
        {
            var parentRow = current;
            var parentIndex = Rows.IndexOf(parentRow);
            if (parentIndex < 0)
                return;

            var childHandle = chain[i];

            // Ensure parent is marked expanded (for UI glyph/state)
            parentRow.IsExpanded = true;
            _expanded.Add(parentRow.Dir);

            // If the child row already exists in the visible rows at the expected depth, use it.
            if (_rowByHandle.TryGetValue(childHandle, out var existing))
            {
                current = existing;
                continue;
            }

            // Ensure child MODEL without materializing siblings (critical).
            if (!_builder.TryEnsureChildNodeFast(parentRow.Model, childHandle, out var childModel))
                return;

            // Insert child row directly under the parent if the subtree isn't already visible.
            // If the next row is already a descendant, we don't insert here (avoid duplicates).
            var nextIndex = parentIndex + 1;
            if (nextIndex < Rows.Count && Rows[nextIndex].Depth > parentRow.Depth)
                // subtree already present; try find the child in the visible range
                current = FindInVisibleSubtree(parentRow, childHandle) ??
                          CreateAndInsertSingleChild(parentRow, childModel);
            else
                current = CreateAndInsertSingleChild(parentRow, childModel);
        }

        SelectRowAndCenter(current);
    }

    private ScanRootsRowViewModel CreateAndInsertSingleChild(ScanRootsRowViewModel parentRow,
        ScanRootsTreeNode childModel)
    {
        var parentIndex = Rows.IndexOf(parentRow);
        var row = CreateRow(childModel, parentRow.Depth + 1);

        Rows.Insert(parentIndex + 1, row);
        return row;
    }

    private ScanRootsRowViewModel? FindInVisibleSubtree(ScanRootsRowViewModel parent, DirHandle wanted)
    {
        var start = Rows.IndexOf(parent);
        if (start < 0)
            return null;

        var parentDepth = parent.Depth;
        for (var i = start + 1; i < Rows.Count && Rows[i].Depth > parentDepth; i++)
            if (Rows[i].Dir.Equals(wanted))
                return Rows[i];

        return null;
    }

    // ---- Ordering ----

    private List<ScanRootsTreeNode> OrderModels(List<ScanRootsTreeNode> models)
    {
        var cmp = GetComparison();

        var list = models.ToList();
        list.Sort((a, b) => cmp(Project(a), Project(b)));

        if (SortDescending)
            list.Reverse();

        return list;

        ScanRootsRowViewModel Project(ScanRootsTreeNode m)
        {
            return new ScanRootsRowViewModel(m, null, null, 0);
        }
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

        var toRestore = _expanded.ToArray();

        BuildFromRoots();

        foreach (var h in toRestore)
            if (_rowByHandle.TryGetValue(h, out var row) && row is { HasLazyChildren: true, IsExpanded: false })
                Expand(row);
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
        if (!_rootIndexByScanRootId.TryGetValue(scanRootId, out var rootIndex))
            return; // already removed / not visible

        if (rootIndex < 0 || rootIndex >= Rows.Count)
        {
            // Map got stale somehow; fall back to a quick rebuild of the map and retry once.
            RebuildRootIndexMap_NoLock();
            if (!_rootIndexByScanRootId.TryGetValue(scanRootId, out rootIndex))
                return;
            if (rootIndex < 0 || rootIndex >= Rows.Count)
                return;
        }

        // Verify root at index (defensive)
        var rootRow = Rows[rootIndex];
        if (rootRow.Depth != 0 || rootRow.ScanRootId != scanRootId)
        {
            // Map stale due to unusual reorder; rebuild and retry once
            RebuildRootIndexMap_NoLock();
            if (!_rootIndexByScanRootId.TryGetValue(scanRootId, out rootIndex))
                return;

            rootRow = Rows[rootIndex];
            if (rootRow.Depth != 0 || rootRow.ScanRootId != scanRootId)
                return;
        }

        // If selection is inside subtree, clear it
        if (SelectedRow is not null && SelectedRow.ScanRootId == scanRootId)
            SelectedRow = null;

        Rows.BeginUpdate();
        try
        {
            // Remove contiguous visible range: root + its descendants until next root (depth 0) or end.
            var removeAt = rootIndex;
            var removedCount = 0;

            while (removeAt < Rows.Count)
            {
                if (removedCount > 0 && Rows[removeAt].Depth == 0)
                    break; // next root => stop

                var row = Rows[removeAt];

                // Clear caches for real handles
                if (row.Dir.IsValid)
                {
                    _expanded.Remove(row.Dir);
                    _rowByHandle.Remove(row.Dir);
                }

                Rows.RemoveAt(removeAt);
                removedCount++;
            }

            // Remove root entry from root-index map
            _rootIndexByScanRootId.Remove(scanRootId);

            // Adjust cached root indices for roots after the removed region
            AdjustRootIndicesAfterMutation_NoLock(startIndexInclusive: rootIndex, delta: -removedCount);
        }
        finally
        {
            Rows.EndUpdate();
        }
    }

    private void AdjustRootIndicesAfterMutation_NoLock(int startIndexInclusive, int delta)
    {
        if (delta == 0 || _rootIndexByScanRootId.Count == 0)
            return;

        // Only roots whose index is >= startIndexInclusive are affected
        // (root rows can be anywhere due to expansions above them).
        var keys = _rootIndexByScanRootId.Keys.ToArray();
        foreach (var key in keys)
        {
            var idx = _rootIndexByScanRootId[key];
            if (idx >= startIndexInclusive)
                _rootIndexByScanRootId[key] = idx + delta;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposer.Dispose();
        return ValueTask.CompletedTask;
    }
}
