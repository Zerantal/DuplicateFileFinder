using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Repository.Core.Models;

// ReSharper disable UnusedParameterInPartialMethod

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTreeFlat;

public sealed partial class ScanRootsFlatTreeViewModel : ObservableObject
{
    private readonly IScanRootsTreeNodeActions _actions;
    private readonly ScanRootsTreeBuilder _builder;

    // Remember expanded handles so Resort/Rebuild can restore expansion state
    private readonly HashSet<DirHandle> _expanded = new();

    // Handle->row for quick selection
    private readonly Dictionary<DirHandle, ScanRootsFlatRowViewModel> _rowByHandle = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedPath))]
    private ScanRootsFlatRowViewModel? _selectedRow;

    private RepoSnapshotView? _snapshot;

    [ObservableProperty] private ScanRootsSortColumn _sortColumn = ScanRootsSortColumn.Size;

    [ObservableProperty] private bool _sortDescending = true;

    public ScanRootsFlatTreeViewModel(ScanRootsTreeBuilder builder, IScanRootsTreeNodeActions actions)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));

        SortByCommand = new RelayCommand<ScanRootsSortColumn>(SortBy);
        ToggleExpandedCommand = new RelayCommand<ScanRootsFlatRowViewModel>(ToggleExpanded);
    }

    // Visible, virtualized rows
    public BulkObservableCollection<ScanRootsFlatRowViewModel> Rows { get; } = [];

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
    public void SelectRowAndCenter(ScanRootsFlatRowViewModel row)
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

        var rootModels =
            _builder.Build(_snapshot); // already size-sorted by builder :contentReference[oaicite:6]{index=6}

        Rows.BeginUpdate();
        try
        {
            Rows.Clear();
            _rowByHandle.Clear();

            foreach (var model in rootModels)
            {
                var row = CreateRow(model, 0);
                Rows.Add(row);
            }

            ApplyRootSort();
        }
        finally
        {
            Rows.EndUpdate();
        }
    }

    private ScanRootsFlatRowViewModel CreateRow(ScanRootsTreeNode model, int depth)
    {
        var row = new ScanRootsFlatRowViewModel(model, _actions, depth);

        if (model.Dir.IsValid)
            _rowByHandle[model.Dir] = row;

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

    private void ToggleExpanded(ScanRootsFlatRowViewModel? row)
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

    private void Expand(ScanRootsFlatRowViewModel row)
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
        }
        finally
        {
            Rows.EndUpdate();
        }
    }

    private void Collapse(ScanRootsFlatRowViewModel row)
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
            while (i < Rows.Count && Rows[i].Depth > parentDepth)
                Rows.RemoveAt(i);
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

    private ScanRootsFlatRowViewModel CreateAndInsertSingleChild(ScanRootsFlatRowViewModel parentRow,
        ScanRootsTreeNode childModel)
    {
        var parentIndex = Rows.IndexOf(parentRow);
        var row = CreateRow(childModel, parentRow.Depth + 1);

        Rows.Insert(parentIndex + 1, row);
        return row;
    }

    private ScanRootsFlatRowViewModel? FindInVisibleSubtree(ScanRootsFlatRowViewModel parent, DirHandle wanted)
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

        ScanRootsFlatRowViewModel Project(ScanRootsTreeNode m)
        {
            return new ScanRootsFlatRowViewModel(m, null, 0);
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

    private Comparison<ScanRootsFlatRowViewModel> GetComparison()
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
}
