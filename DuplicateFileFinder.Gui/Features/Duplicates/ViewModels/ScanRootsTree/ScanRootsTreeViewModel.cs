// DuplicateFileFinder.Gui/Features/Duplicates/ViewModels/ScanRootsTree/ScanRootsTreeViewModel.cs

using System.Collections.ObjectModel;
using System.Windows.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

/// <summary>
/// View-model for the tree-table that shows scan roots and their directory tree,
/// with aggregate stats supplied by indexes.
/// </summary>
public sealed partial class ScanRootsTreeViewModel : ObservableObject
{
    private readonly ScanRootsTreeBuilder _builder;
    private readonly FolderNodeViewModelFactory _factory;

    private RepoSnapshotView? _snapshot;

    // DirHandle -> currently materialized VM
    private readonly Dictionary<DirHandle, FolderNodeViewModel> _vmByHandle = new();

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

    public ScanRootsTreeViewModel(ScanRootsTreeBuilder builder, FolderNodeViewModelFactory factory)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        SortByCommand = new RelayCommand<ScanRootsSortColumn>(SortBy);
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnSortColumnChanged(ScanRootsSortColumn value) => OnSortHeaderChanged();
    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnSortDescendingChanged(bool value) => OnSortHeaderChanged();

    public void Rebuild(RepoSnapshotView snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _vmByHandle.Clear();

        var rootModels = _builder.Build(_snapshot);

        Roots.Clear();
        foreach (var rootModel in rootModels)
        {
            var rootVm = _factory.CreateVm(
                model: rootModel,
                parent: null,
                snapshot: _snapshot,
                register: RegisterVm);

            Roots.Add(rootVm);
        }

        ResortAll();
    }

    private void RegisterVm(FolderNodeViewModel vm)
    {
        // Only register valid handles; placeholders are invalid and shouldn't participate in navigation.
        if (!vm.Dir.IsValid)
            return;

        _vmByHandle[vm.Dir] = vm;
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
        if (_snapshot is null)
            return;

        // Build chain: scanroot -> ... -> target
        if (!_builder.TryBuildAncestorChainToScanRoot(target, out var chain))
            return;

        // Chain[0] is scanroot dir handle. It MUST already be a root VM.
        if (!_vmByHandle.TryGetValue(chain[0], out var current))
            return;

        // Expand/materialize down the chain.
        for (var i = 1; i < chain.Count; i++)
        {
            // Ensure parent expanded (triggers lazy load via EnsureChildrenLoaded)
            current.IsExpanded = true;

            // If we already have the child VM in the map, use it.
            if (_vmByHandle.TryGetValue(chain[i], out var existingChild))
            {
                current = existingChild;
                continue;
            }

            // Otherwise, ensure the *model* exists under parent and create the VM.
            if (!current.Model.ChildrenMaterialized)
                _builder.EnsureChildrenLoaded(current.Model);

            var childModel = current.Model.Children.FirstOrDefault(m => m.Dir.Equals(chain[i]));
            if (childModel is null)
            {
                // Fallback: builder can create/attach missing model (e.g. if parent wasn't materialized yet).
                childModel = _builder.EnsureNodeExistsUnderParent(current.Model, chain[i]);
        }

            // Ensure the UI children collection contains the child VM (without duplicating work).
            if (current.HasDummyChild)
                current.ClearChildren();

            var childVm = _factory.CreateVm(childModel, current, _snapshot, register: RegisterVm);
            InsertSortedBySizeDesc(current.Children, childVm);

            current = childVm;
        }

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

    private static void InsertSortedBySizeDesc(ObservableCollection<FolderNodeViewModel> list, FolderNodeViewModel node)
    {
        var i = 0;
        while (i < list.Count && list[i].TotalBytes > node.TotalBytes)
            i++;

        list.Insert(i, node);
    }
}
