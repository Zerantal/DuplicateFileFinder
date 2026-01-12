// Features/Controller/ViewModels/Controller/DuplicateGroupsViewModel.cs

using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Domain;
using DuplicateFileFinder.Gui.Features.Duplicates.Models;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.Duplicates;

public partial class DuplicateGroupsViewModel : ObservableObject
{
    public DuplicateGroups.DuplicatesController Controller { get; }
    private readonly IRepo _repo;
    private readonly IFileDirReadModel _fileDirIndex;
    private readonly IDialogService _dialogs;
    private readonly IFileSystemDeleteService _deleter;

    private static readonly ReadOnlyObservableCollection<FileItem> EmptyItems =
        new(new ObservableCollection<FileItem>());

    public DuplicateGroupsViewModel(
        IRepoHost host,
        IScanCoordinator scanner,
        IDialogService dialogs,
        IFileSystemDeleteService deleter)
    {
        _repo = host.Repo;
        _fileDirIndex = host.FileDirIndex;
        _dialogs = dialogs;
        _deleter = deleter;

        Controller = new DuplicateGroups.DuplicatesController(host, host.HashIndex);
        Controller.PropertyChanged += (_, e) =>
        {
            // bubble up the things the view binds to
            if (e.PropertyName is nameof(DuplicateGroups.DuplicatesController.FilteredSets))
                OnPropertyChanged(nameof(FilteredSets));

            if (e.PropertyName is nameof(DuplicateGroups.DuplicatesController.DuplicatesFound))
                OnPropertyChanged(nameof(DuplicatesFound));

            if (e.PropertyName is nameof(DuplicateGroups.DuplicatesController.FilesScanned))
                OnPropertyChanged(nameof(FilesScanned));

            if (e.PropertyName is nameof(DuplicateGroups.DuplicatesController.WastedBytes))
                OnPropertyChanged(nameof(WastedBytes));

            if (e.PropertyName is nameof(DuplicateGroups.DuplicatesController.SelectedSet))
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
            OnPropertyChanged(nameof(SelectedSet));
            OnPropertyChanged(nameof(SelectedItems));
        }
    }

    public ReadOnlyObservableCollection<FileItem> SelectedItems
        => SelectedSet?.Items ?? EmptyItems;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedDuplicateFileCommand))]
    private FileItem? _selectedDuplicateFile;

    private bool CanDeleteSelectedDuplicateFile() => SelectedDuplicateFile is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedDuplicateFile))]
    private Task DeleteSelectedDuplicateFileAsync()
        => DeleteDuplicateFileAsync(SelectedDuplicateFile);

    // This is your revised clearer version + optimistic UI update at the end
    private async Task DeleteDuplicateFileAsync(FileItem? item)
    {
        if (item is null)
            return;

        var fullPath = item.Value.FullPath;
        if (string.IsNullOrWhiteSpace(fullPath))
            return;

        var ok = await _dialogs.ShowConfirmationAsync(
            "Delete file",
            $"Delete this file from disk?\n\n{fullPath}",
            okText: "Delete",
            cancelText: "Cancel");

        if (!ok)
            return;

        var (deleted, deleteErr) = await _deleter.DeleteFileAsync(fullPath);
        if (!deleted)
        {
            await _dialogs.ShowErrorAsync("Delete failed", deleteErr ?? "Unknown error.");
            return;
        }

        if (!_fileDirIndex.TryGetFile(item.Value.Id, out var fileHandle))
        {
            await _dialogs.ShowErrorAsync(
                "Delete error",
                "Deleted file from disk, but could not resolve the file handle in the index. " +
                "The repository may still show the file until the next rescan/rebuild.");
            return;
        }

        var repoResult = await _repo.DeleteFileAsync(fileHandle);
        if (!repoResult.Success)
        {
            await _dialogs.ShowErrorAsync(
                "Delete error",
                $"Deleted file from disk, but deleting entry from repository failed: {repoResult.Error}");
            return;
        }

        // optimistic: update selected set immediately
        SelectedSet?.TryRemoveItemByFileId(item.Value.Id);

        if (SelectedDuplicateFile.Equals(item))
            SelectedDuplicateFile = null;

        // Optional: if the group is no longer a duplicate group, clear selection.
        if (SelectedSet is { } set && set.Items.Count < 2)
            SelectedSet = null;
    }
}
