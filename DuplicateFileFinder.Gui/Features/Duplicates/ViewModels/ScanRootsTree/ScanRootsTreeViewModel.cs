using System.Collections.ObjectModel;
using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Domain;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

/// <summary>
/// View-model for the tree-table that shows scan roots and their directory tree,
/// with aggregate stats supplied by indexes.
/// </summary>
public sealed partial class ScanRootsTreeViewModel : ObservableObject
{
    private readonly ScanRootsTreeBuilder _builder;

    public ObservableCollection<FolderNodeViewModel> Roots { get; } = [];

    private FolderNodeViewModel? _selectedNode;

    public FolderNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value))
                return;

            _selectedNode = value;
            OnPropertyChanged();
            SelectedPath = _selectedNode?.FullPath;
        }
    }

    [ObservableProperty]
    private string? _selectedPath;

    // ---- Sorting ----

    private ScanRootsSortColumn _sortColumn = ScanRootsSortColumn.Size;
    private bool _sortDescending = true;

    public ScanRootsSortColumn SortColumn
    {
        get => _sortColumn;
        private set
        {
            if (_sortColumn == value)
                return;

            _sortColumn = value;
            OnPropertyChanged();
            OnSortHeaderChanged();
        }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        private set
        {
            if (_sortDescending == value)
                return;

            _sortDescending = value;
            OnPropertyChanged();
            OnSortHeaderChanged();
        }
    }

    public ICommand SortByCommand { get; }

    public ScanRootsTreeViewModel(ScanRootsTreeBuilder builder)
    {
        _builder = builder;
        SortByCommand = new RelayCommand<ScanRootsSortColumn>(SortBy);
    }

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

        Comparison<FolderNodeViewModel> cmp = SortColumn switch
        {
            ScanRootsSortColumn.Name => (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            ScanRootsSortColumn.Size => (a, b) => a.TotalBytes.CompareTo(b.TotalBytes),
            ScanRootsSortColumn.Items => (a, b) => a.ItemCount.CompareTo(b.ItemCount),
            ScanRootsSortColumn.Files => (a, b) => a.FileCount.CompareTo(b.FileCount),
            ScanRootsSortColumn.DupFiles => (a, b) => a.DuplicateFiles.CompareTo(b.DuplicateFiles),
            ScanRootsSortColumn.DupBytes => (a, b) => a.DuplicateBytes.CompareTo(b.DuplicateBytes),
            _ => (_, _) => 0
        };

        var list = nodes.ToList();
        list.Sort(cmp);
        if (SortDescending)
            list.Reverse();

        nodes.Clear();
        foreach (var n in list)
            nodes.Add(n);
    }

    // ---- Sort indicators ----

    private string ArrowFor(ScanRootsSortColumn column)
    {
        if (SortColumn != column)
            return string.Empty;

        return SortDescending ? " ▼" : " ▲";
    }

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

    public void NavigateToDir(DirHandle dirHandle)
        => NavigateToDirHandleLazy(dirHandle);

    public void NavigateToFile(FileHandle fileHandle)
    {
        if (_builder.TryGetParentDirHandle(fileHandle, out var parent))
            NavigateToDirHandleLazy(parent);
    }

    /// <summary>
    /// Convenience for TreeMap: navigate to a directory by dirId.
    /// </summary>
    public void NavigateToDirId(long dirId)
    {
        if (_builder.TryGetDirHandle(dirId, out var handle))
            NavigateToDirHandleLazy(handle);
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
