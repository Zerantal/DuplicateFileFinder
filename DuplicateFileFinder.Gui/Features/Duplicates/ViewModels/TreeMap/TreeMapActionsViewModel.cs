using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public partial class TreeMapActionsViewModel : ObservableObject
{
    private readonly IScanCoordinator _scanner;

    public TreeMapActionsViewModel(IScanCoordinator scanner)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        _scanner = scanner;
    }

    [ObservableProperty]
    private DirHandle _hoverFolder;

    public bool IsHoveringFolder => HoverFolder.IsValid;

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnHoverFolderChanged(DirHandle value)
    {
        OnPropertyChanged(nameof(IsHoveringFolder));
        RescanFolderCommand.NotifyCanExecuteChanged();
    }

    private bool CanRescanHoverFolder() => HoverFolder.IsValid;

    [RelayCommand(CanExecute = nameof(CanRescanHoverFolder))]
    private Task RescanFolderAsync()
        => _scanner.RunFolderRescanWithDialogAsync(HoverFolder);
}
