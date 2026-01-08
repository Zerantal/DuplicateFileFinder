using System.Collections.ObjectModel;
using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Domain;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

/// <summary>
/// View-model for the tree-table that shows scan roots and their directory tree,
/// with aggregate stats supplied by indexes.
/// </summary>
public sealed partial class ScanRootsTreeViewModel : ObservableObject
{
    private readonly ScanRootsTreeBuilder _builder;

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = [];

    // ---- Selection ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPath))]
    private FolderNodeViewModel? _selectedNode;

    public string? SelectedPath => SelectedNode?.FullPath;

    // ---- Sorting ----

    [ObservableProperty]
    private ScanRootsSortColumn _sortColumn = ScanRootsSortColumn.Size;

    [ObservableProperty]
    private bool _sortDescending = true;

    public ICommand SortByCommand { get; }

    public ScanRootsTreeViewModel(ScanRootsTreeBuilder builder)
    {
        _builder = builder;
        SortByCommand = new RelayCommand<ScanRootsSortColumn>(SortBy);
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnSortColumnChanged(ScanRootsSortColumn value) => OnSortHeaderChanged();
    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnSortDescendingChanged(bool value) => OnSortHeaderChanged();

    public void Rebuild(RepoSnapshotView snapshot)
    {
        _builder.Rebuild(snapshot, Roots);

        // Keep whatever sort the user chose.
        ResortAll();
    }

    private void SortBy(ScanRootsSortColumn column)
    {
        if (SortColumn == column)
            SortDescending = !SortDescending;
        else
        {
            SortColumn = column;
            SortDescending = true;
        }

        ResortAll();
    }

    private void ResortAll()
    {
        SortInPlace(Roots);

        foreach (var root in Roots)
            SortRecursively(root);
    }

    private void SortRecursively(FolderNodeViewModel node)
    {
        SortInPlace(node.Children);
        foreach (var child in node.Children)
            SortRecursively(child);
    }

    private void SortInPlace(ObservableCollection<FolderNodeViewModel> nodes)
    {
        if (nodes.Count <= 1)
            return;

        var cmp = GetComparison();

        var list = nodes.ToList();
        list.Sort(cmp);
        if (SortDescending)
            list.Reverse();

        nodes.Clear();
        foreach (var n in list)
            nodes.Add(n);
    }

    private Comparison<FolderNodeViewModel> GetComparison()
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

    // ---- Sort indicators ----

    private string ArrowFor(ScanRootsSortColumn column)
        => SortColumn != column ? string.Empty : (SortDescending ? " ▼" : " ▲");

    public string NameArrow => ArrowFor(ScanRootsSortColumn.Name);
    public string SizeArrow => ArrowFor(ScanRootsSortColumn.Size);
    public string ItemsArrow => ArrowFor(ScanRootsSortColumn.Items);
    public string FilesArrow => ArrowFor(ScanRootsSortColumn.Files);
    public string DupFilesArrow => ArrowFor(ScanRootsSortColumn.DupFiles);
    public string DupBytesArrow => ArrowFor(ScanRootsSortColumn.DupBytes);

    private void OnSortHeaderChanged()
    {
        OnPropertyChanged(nameof(NameArrow));
        OnPropertyChanged(nameof(SizeArrow));
        OnPropertyChanged(nameof(ItemsArrow));
        OnPropertyChanged(nameof(FilesArrow));
        OnPropertyChanged(nameof(DupFilesArrow));
        OnPropertyChanged(nameof(DupBytesArrow));
    }

    // ---- Navigation (lazy-safe) ----

    public void NavigateToDir(DirHandle dirHandle) => NavigateToDirHandleLazy(dirHandle);

    public void NavigateToFile(FileHandle fileHandle)
    {
        if (_builder.TryGetParentDirHandle(fileHandle, out var parent))
            NavigateToDirHandleLazy(parent);
    }

    private void NavigateToDirHandleLazy(DirHandle target)
    {
        // Build chain: scanroot -> ... -> target
        if (!_builder.TryBuildAncestorChainToScanRoot(target, out var chain))
            return;

        // Chain[0] is scanroot dir handle, which must exist as a root node.
        if (!_builder.TryGetNode(chain[0], out var current))
            return;

        // Expand/materialize down the chain.
        for (var i = 1; i < chain.Count; i++)
        {
            // Ensure parent expanded (triggers lazy load)
            current.IsExpanded = true;

            // Ensure the child exists even if it wasn't materialized yet.
            current = _builder.EnsureNodeExistsUnderParent(current, chain[i]);
        }

        // Expand-to ensures UI shows the selection; selection sets SelectedPath.
        ExpandTo(current);
        SelectedNode = current;
    }

    private static void ExpandTo(FolderNodeViewModel node)
    {
        var stack = new Stack<FolderNodeViewModel>();

        for (var cur = node; cur != null; cur = cur.Parent)
            stack.Push(cur);

        while (stack.Count > 0)
            stack.Pop().IsExpanded = true;
    }
}
