// Features/Controller/ViewModels/Controller/DuplicateGroupsViewModel.cs

using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Infrastructure.Converters;
using DuplicateFileFinder.Gui.Infrastructure.Status;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Logging;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;

public partial class DuplicateGroupsViewModel : ObservableObject, IStatusProvider
{
    private static readonly ReadOnlyObservableCollection<FileItem> s_emptyItems = new([]);
    private readonly DuplicateGroupsController _controller;
    private readonly IDuplicateFileDeletionService _deleteService;
    private readonly IHashIndexReadModel _hashIndex;
    private readonly ITreeIndexReadModel _treeIndex;

    private DuplicateQuery _query = DuplicateQuery.Default;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedDuplicateFileCommand))]
    private FileItem? _selectedDuplicateFile;

    [ObservableProperty] private DirHandle? _selectedSubtreeDir;

    public DuplicateGroupsViewModel(
        IRepoHost repoHost,
        DuplicateGroupsController controller,
        IDuplicateFileDeletionService deleteService)
    {
        ArgumentNullException.ThrowIfNull(repoHost);

        _hashIndex = repoHost.HashIndex;
        _treeIndex = repoHost.TreeIndex;
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _deleteService = deleteService ?? throw new ArgumentNullException(nameof(deleteService));

        _controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DuplicateGroupsController.DuplicatesFound) ||
                e.PropertyName is nameof(DuplicateGroupsController.FilesScanned) ||
                e.PropertyName is nameof(DuplicateGroupsController.WastedBytes))
            {
                OnPropertyChanged(nameof(DuplicatesFound));
                OnPropertyChanged(nameof(FilesScanned));
                OnPropertyChanged(nameof(WastedBytes));
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        };

        PagedSets = new PagingList<DuplicateSetRow>(
            300,
            FetchSetsPage);

        // initial load
        PagedSets.EnsureLoadedThroughIndex(0);
    }

    public PagingList<DuplicateSetRow> PagedSets { get; }

    public int DuplicatesFound => _controller.DuplicatesFound;
    public int FilesScanned => _controller.FilesScanned;
    public long WastedBytes => _controller.WastedBytes;

    public DuplicateQuery Query
    {
        get => _query;
        set
        {
            if (_query.Equals(value))
                return;

            _query = value;

            SelectedSet = null;
            PagedSets.Reset();
            PagedSets.EnsureLoadedThroughIndex(0);
        }
    }

    public DuplicateSetRow? SelectedSet
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;

            field?.IsSelected = false;

            field = value;

            if (field is not null)
            {
                field.IsSelected = true;

                // Materialize the file list only now.
                if (field.Descriptor != default)
                {
                    var files = _controller.ResolveFiles(field.Descriptor);
                    field.SetItems(files);
                }
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedItems));
        }
    }

    public ReadOnlyObservableCollection<FileItem> SelectedItems
        => SelectedSet?.Items ?? s_emptyItems;

    public event EventHandler? StatusChanged;

    public IReadOnlyList<StatusItem> GetStatusItems()
    {
        // Format here so MainWindowVM stays dumb.
        return
        [
            new StatusItem("Files scanned", FilesScanned.ToString("N0", CultureInfo.CurrentUICulture)),
            new StatusItem("Duplicates", DuplicatesFound.ToString("N0", CultureInfo.CurrentUICulture)),
            new StatusItem("Space wasted", BytesToHuman(WastedBytes))
        ];
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnSelectedSubtreeDirChanged(DirHandle? value)
    {
        SelectedSet = null;
        PagedSets.Reset();
        PagedSets.EnsureLoadedThroughIndex(0);
    }

    // Called by parent VM when snapshot changes
    public void Rebuild(RepoSnapshotView snapshot)
    {
        _controller.Rebuild(snapshot);

        // Clear selection (old rows may be stale).
        SelectedSet = null;

        // Re-query pages.
        PagedSets.Reset();
        PagedSets.EnsureLoadedThroughIndex(0);

        // Update status bar.
        OnPropertyChanged(nameof(DuplicatesFound));
        OnPropertyChanged(nameof(FilesScanned));
        OnPropertyChanged(nameof(WastedBytes));
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private (int total, DuplicateSetRow[] items) FetchSetsPage(int offset, int count)
    {
        if (SelectedSubtreeDir is not { IsValid: true } subtree)
            return FetchSetsPage_Unfiltered(offset, count);

        if (!_treeIndex.TryGetSubtreeRange(subtree, out var range) || range.IsEmpty)
            return (-1, []);

        var filter = new SubtreeFilter(subtree, range);
        return FetchSetsPage_Filtered(filter, offset, count);
    }

    private (int total, DuplicateSetRow[] items) FetchSetsPage_Unfiltered(int offset, int count)
    {
        var page = _hashIndex.GetGroupsPage(_query, offset, count);
        if (page.Count == 0)
            return (-1, []);

        var rows = new DuplicateSetRow[page.Count];
        var span = page.Groups.Span;

        for (var i = 0; i < page.Count; i++)
            rows[i] = new DuplicateSetRow(
                span[i],
                _controller.ResolveFileName);

        return (-1, rows);
    }

    private (int total, DuplicateSetRow[] items) FetchSetsPage_Filtered(
        in SubtreeFilter filter,
        int offset,
        int count)
    {
        DuplicateSetRow[] rows;

        using (TimingLog.Start("FetchSetsPage_Filtered"))
        {
            var page = _hashIndex.GetGroupsPage(_query, filter, offset, count);
            if (page.Count == 0)
                return (-1, []);

            rows = new DuplicateSetRow[page.Count];
            var span = page.Groups.Span;

            for (var i = 0; i < page.Count; i++)
                rows[i] = new DuplicateSetRow(
                    span[i],
                    _controller.ResolveFileName);
        }

        return (-1, rows);
    }

    // Called by view when scrolling approaches end
    public void OnNearEnd(int lastRealizedIndex)
        => PagedSets.EnsureLoadedThroughIndex(lastRealizedIndex);

    private bool CanDeleteSelectedDuplicateFile() => SelectedDuplicateFile is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedDuplicateFile))]
    private Task DeleteSelectedDuplicateFileAsync()
        => DeleteDuplicateFileAsync(SelectedDuplicateFile);

    private async Task DeleteDuplicateFileAsync(FileItem? item)
    {
        if (item is null)
            return;

        var result = await _deleteService.DeleteAsync(item.Value.Id, item.Value.FullPath);
        if (!result.Success)
            return;

        // Optimistic: update UI immediately.
        SelectedSet?.TryRemoveItemByFileId(item.Value.Id);

        if (SelectedDuplicateFile.Equals(item))
            SelectedDuplicateFile = null;

        if (SelectedSet is { Count: < 2 })
            SelectedSet = null;
    }

    private static string BytesToHuman(long bytes)
    {
        return (string?)BytesToHumanConverter.Instance.Convert(
                   bytes, typeof(string), null, CultureInfo.CurrentUICulture)
               ?? $"{bytes:n0} bytes";
    }
}
