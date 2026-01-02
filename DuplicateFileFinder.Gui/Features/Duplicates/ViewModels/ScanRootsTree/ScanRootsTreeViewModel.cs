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

        // optional: initial sort after first build
    }

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

    public void Rebuild(RepoSnapshotView snapshot)
        => _builder.Rebuild(snapshot, Roots);

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

        ResortAll();
    }

    private void ResortAll()
    {
        SortCollection(Roots);

        foreach (var root in Roots)
            SortRecursively(root);
    }

    private void SortRecursively(FolderNodeViewModel node)
    {
        SortCollection(node.Children);

        foreach (var child in node.Children)
            SortRecursively(child);
    }

    private void SortCollection(IList<FolderNodeViewModel> nodes)
    {
        var sorted = nodes
            .OrderBy(_ => 0) // placeholder
            .ToList();

        sorted = SortColumn switch
        {
            ScanRootsSortColumn.Name =>
                sorted.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList(),

            ScanRootsSortColumn.Size =>
                sorted.OrderBy(n => n.TotalBytes).ToList(),

            ScanRootsSortColumn.Items =>
                sorted.OrderBy(n => n.ItemCount).ToList(),

            ScanRootsSortColumn.Files =>
                sorted.OrderBy(n => n.FileCount).ToList(),

            ScanRootsSortColumn.DupFiles =>
                sorted.OrderBy(n => n.DuplicateFiles).ToList(),

            ScanRootsSortColumn.DupBytes =>
                sorted.OrderBy(n => n.DuplicateBytes).ToList(),

            _ => sorted
        };

        if (SortDescending)
            sorted.Reverse();

        nodes.Clear();
        foreach (var n in sorted)
            nodes.Add(n);
    }

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

}
