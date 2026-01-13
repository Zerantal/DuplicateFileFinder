using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public partial class TreeMapActionsViewModel : ObservableObject
{
    private readonly IRepo _repo;
    private readonly IFileDirReadModel _fileDir;
    private readonly IScanCoordinator _scanner;
    private readonly IDialogService _dialogs;
    private readonly IFileSystemDeleteService _deleter;

    private Dictionary<long, string> _scanRootFullPathById = new();

    public TreeMapActionsViewModel(
        IRepoHost host,
        IScanCoordinator scanner,
        IDialogService dialogs,
        IFileSystemDeleteService deleter,
        DisposableManager disposer)
    {
        _repo = host.Repo;
        _fileDir = host.FileDirIndex;
        _scanner = scanner;
        _dialogs = dialogs;
        _deleter = deleter;

        RebuildScanRootPaths();

        EventHandler<RepoIndexesRebuiltEventArgs> handler = (_, _) => RebuildScanRootPaths();
        host.IndexesRebuilt += handler;
        disposer.Add(() => host.IndexesRebuilt -= handler);
    }

    private void RebuildScanRootPaths()
    {
        _scanRootFullPathById = _repo.ScanRootsView
            .Where(r => !r.IsDeleted)
            .ToDictionary(
                r => r.RootId,
                r => r.VolumePath != null
                    ? Path.Combine(r.VolumePath, r.RootPath)
                    : r.RootPath);
    }

    // Set by view when context menu opens/closes (based on TreeMapControl.SelectedNode)
    [NotifyPropertyChangedFor(nameof(HasContextTarget))]
    [NotifyPropertyChangedFor(nameof(IsContextDir))]
    [NotifyPropertyChangedFor(nameof(IsContextFile))]
    [NotifyCanExecuteChangedFor(nameof(RescanSelectedFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [ObservableProperty]
    private ITreeMapNodeElement? _contextTarget;

    public bool HasContextTarget => ContextTarget is not null;
    public bool IsContextDir => ContextTarget is DirTreeMapElement;
    public bool IsContextFile => ContextTarget is FileTreeMapElement;

    private bool CanRescanSelectedFolder() => ContextTarget is DirTreeMapElement;
    private bool CanDeleteSelected() => ContextTarget is DirTreeMapElement or FileTreeMapElement;

    [RelayCommand(CanExecute = nameof(CanRescanSelectedFolder))]
    private Task RescanSelectedFolderAsync()
        => _scanner.RunFolderRescanWithDialogAsync((ContextTarget as DirTreeMapElement)!.Dir);


    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelectedAsync()
    {
        return ContextTarget switch
        {
            DirTreeMapElement d => DeleteDirAsync(d),
            FileTreeMapElement f => DeleteFileAsync(f),
            _ => Task.CompletedTask
        };
    }

    private async Task DeleteDirAsync(DirTreeMapElement dirElement)
    {
        if (!_fileDir.TryGetDirPathByHandle(dirElement.Dir, out var rel) || string.IsNullOrWhiteSpace(rel))
        {
            await _dialogs.ShowErrorAsync("Delete failed", "Could not resolve folder path from index.");
            return;
        }

        if (!_scanRootFullPathById.TryGetValue(dirElement.Dir.ScanRootId, out var root))
        {
            await _dialogs.ShowErrorAsync("Delete failed", "Could not resolve scan root path.");
            return;
        }

        var fullPath = Path.Combine(root, rel);

        var ok = await _dialogs.ShowConfirmationAsync(
            "Delete folder",
            $"Delete this folder from disk?\n\n{fullPath}",
            okText: "Delete",
            cancelText: "Cancel");

        if (!ok) return;

        var (deleted, err) = await _deleter.DeleteDirectoryAsync(fullPath, recursive: true);
        if (!deleted)
        {
            await _dialogs.ShowErrorAsync("Delete failed", err ?? "Unknown error.");
            return;
        }

        var result = await _repo.DeleteDirAsync(dirElement.Dir);
        if (!result.Success)
        {
            await _dialogs.ShowErrorAsync(
                "Delete error",
                $"Deleting entry from repository failed: {result.Error}");
        }
    }

    private async Task DeleteFileAsync(FileTreeMapElement fileElement)
    {
        if (!_fileDir.TryGetFilePathByHandle(fileElement.File, out var rel) || string.IsNullOrWhiteSpace(rel))
        {
            await _dialogs.ShowErrorAsync("Delete failed", "Could not resolve file path from index.");
            return;
        }

        if (!_scanRootFullPathById.TryGetValue(fileElement.File.ScanRootId, out var root))
        {
            await _dialogs.ShowErrorAsync("Delete failed", "Could not resolve scan root path.");
            return;
        }

        var fullPath = Path.Combine(root, rel);

        var ok = await _dialogs.ShowConfirmationAsync(
            "Delete file",
            $"Delete this file from disk?\n\n{fullPath}",
            okText: "Delete",
            cancelText: "Cancel");

        if (!ok) return;

        var (deleted, err) = await _deleter.DeleteFileAsync(fullPath);
        if (!deleted)
        {
            await _dialogs.ShowErrorAsync("Delete failed", err ?? "Unknown error.");
            return;
        }

        var result = await _repo.DeleteFileAsync(fileElement.File);
        if (!result.Success)
        {
            await _dialogs.ShowErrorAsync(
                "Delete error",
                $"Deleting entry from repository failed: {result.Error}");
        }
    }
}
