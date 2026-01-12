// Features/Controller/ViewModels/Controller/DuplicateGroupsViewModel.cs

using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;

public partial class DuplicateGroupsViewModel : ObservableObject
{
    public DuplicateGroupsController Controller { get; }
    private readonly IDuplicateFileDeletionService _deleteService;

    private static readonly ReadOnlyObservableCollection<FileItem> s_emptyItems = new([]);

    public DuplicateGroupsViewModel(
        DuplicateGroupsController controller,
        IDuplicateFileDeletionService deleteService)
    {
        _deleteService = deleteService ?? throw new ArgumentNullException(nameof(deleteService));

        Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        Controller.PropertyChanged += (_, e) =>
        {
            // bubble up the things the view binds to
            if (e.PropertyName is nameof(Controller.FilteredSets))
                OnPropertyChanged(nameof(FilteredSets));

            if (e.PropertyName is nameof(Controller.DuplicatesFound))
                OnPropertyChanged(nameof(DuplicatesFound));

            if (e.PropertyName is nameof(Controller.FilesScanned))
                OnPropertyChanged(nameof(FilesScanned));

            if (e.PropertyName is nameof(Controller.WastedBytes))
                OnPropertyChanged(nameof(WastedBytes));

            if (e.PropertyName is nameof(Controller.SelectedSet))
            {
                OnPropertyChanged(nameof(SelectedSet));
                OnPropertyChanged(nameof(SelectedItems));
            }
        };
    }

    // Called by parent VM when snapshot changes
    public void Rebuild(RepoSnapshotView snapshot)
        => Controller.Rebuild(snapshot);

    public BulkObservableCollection<DuplicateSetRow> FilteredSets => Controller.FilteredSets;

    public int DuplicatesFound => Controller.DuplicatesFound;
    public int FilesScanned => Controller.FilesScanned;
    public long WastedBytes => Controller.WastedBytes;

    public string? SelectedFolderPrefix
    {
        get => Controller.SelectedFolderPrefix;
        set => Controller.SelectedFolderPrefix = value;
    }

    public DuplicateSetRow? SelectedSet
    {
        get => Controller.SelectedSet;
        set
        {
            if (ReferenceEquals(Controller.SelectedSet, value))
                return;

            Controller.SelectedSet = value;
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

        // optimistic: update selected set immediately
        SelectedSet?.TryRemoveItemByFileId(item.Value.Id);

        if (SelectedDuplicateFile.Equals(item))
            SelectedDuplicateFile = null;

        if (SelectedSet is { Items.Count: < 2 })
            SelectedSet = null;
    }
}
