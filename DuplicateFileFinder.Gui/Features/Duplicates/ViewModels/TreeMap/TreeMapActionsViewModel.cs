using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Application.Deletion;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;

using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

using NLog;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public partial class TreeMapActionsViewModel : ObservableObject
{
    private readonly IRepo _repo;
    private readonly IFileDirReadModel _fileDir;
    private readonly IScanCoordinator _scanner;
    private readonly IDeletionWorkflowService _deletionService;

    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private Dictionary<ScanRootId, string> _scanRootFullPathById = new();

    public TreeMapActionsViewModel(
        IRepoHost host,
        IScanCoordinator scanner,
        IDeletionWorkflowService deletionService,
        DisposableManager disposer)
    {
        _repo = host.Repo;
        _fileDir = host.FileDirIndex;
        _scanner = scanner;
        RebuildScanRootPaths();

        EventHandler<RepoIndexesRebuiltEventArgs> handler = (_, _) => RebuildScanRootPaths();
        host.IndexesRebuilt += handler;
        disposer.Add(() => host.IndexesRebuilt -= handler);

        _deletionService = deletionService ?? throw new ArgumentNullException(nameof(deletionService));
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
            s_log.Warn("TreeMap delete skipped: could not resolve relative path for dir {DirHandle}.", dirElement.Dir);
            return;
        }

        if (!_scanRootFullPathById.TryGetValue(dirElement.Dir.ScanRootId, out var root))
        {
            s_log.Warn("TreeMap delete skipped: could not resolve scan root path for root {ScanRootId}.", dirElement.Dir.ScanRootId);
            return;
        }

        var fullPath = Path.Combine(root, rel);

        await _deletionService.DeleteFolderAsync(dirElement.Dir, fullPath);
    }

    private async Task DeleteFileAsync(FileTreeMapElement fileElement)
    {
        if (!_fileDir.TryGetFilePathByHandle(fileElement.File, out var rel) || string.IsNullOrWhiteSpace(rel))
        {
            s_log.Warn("TreeMap delete skipped: could not resolve relative path for file {FileHandle}.", fileElement.File);
            return;
        }

        if (!_scanRootFullPathById.TryGetValue(fileElement.File.ScanRootId, out var root))
        {
            s_log.Warn("TreeMap delete skipped: could not resolve scan root path for root {ScanRootId}.", fileElement.File.ScanRootId);
            return;
        }

        var fullPath = Path.Combine(root, rel);

        await _deletionService.DeleteFileAsync(fileElement.File, fullPath);
    }
}
