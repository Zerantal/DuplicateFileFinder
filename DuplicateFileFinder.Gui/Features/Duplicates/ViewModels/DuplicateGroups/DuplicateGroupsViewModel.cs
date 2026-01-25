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

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;

public partial class DuplicateGroupsViewModel : ObservableObject, IStatusProvider
{
    private readonly IHashIndexReadModel _hashIndex;
    private readonly DuplicateGroupsController _controller;
    private readonly IDuplicateFileDeletionService _deleteService;

    private static readonly ReadOnlyObservableCollection<FileItem> s_emptyItems = new([]);

    public PagingList<DuplicateSetRow> PagedSets { get; }

    private DuplicateQuery _query = new();

    public DuplicateGroupsViewModel(
        IRepoHost repoHost,
        DuplicateGroupsController controller,
        IDuplicateFileDeletionService deleteService)
    {
        ArgumentNullException.ThrowIfNull(repoHost);

        _hashIndex = repoHost.HashIndex;
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
            pageSize: 300,
            fetchPage: FetchSetsPage);

        // initial load
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

    private (int total, DuplicateSetRow[] items) FetchSetsPage(int offset, int count)
    {
        var page = _hashIndex.GetGroupsPage(_query, offset, count);
        if (page.Count == 0)
            return (page.Total, Array.Empty<DuplicateSetRow>());

        var rows = new DuplicateSetRow[page.Count];
        var span = page.Groups.Span;

        for (int i = 0; i < page.Count; i++)
        {
            var d = span[i];
            rows[i] = new DuplicateSetRow(
                descriptor: d,
                nameResolver: _controller.ResolveFileName
                );
        }

        return (page.Total, rows);
    }

    // Called by view when scrolling approaches end
    public void OnNearEnd(int lastRealizedIndex)
        => PagedSets.EnsureLoadedThroughIndex(lastRealizedIndex);

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedDuplicateFileCommand))]
    private FileItem? _selectedDuplicateFile;

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

    private static string BytesToHuman(long bytes)
    {
        return (string?)BytesToHumanConverter.Instance.Convert(
                   bytes, typeof(string), null, CultureInfo.CurrentUICulture)
               ?? $"{bytes:n0} bytes";
    }
}
