// ViewModels/FolderNodeViewModel.cs

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateFileFinder.Gui.Services;
using DuplicateFileFinderLib.Core;

namespace DuplicateFileFinder.Gui.ViewModels;

public sealed partial class FolderNodeViewModel : ObservableObject
{
    private readonly IScanCoordinator _scanCoordinator;

    private string _fullPath;

    private string _name;

    private bool _showFullPath;

    public FolderNodeViewModel(
        Guid dirId,
        string name,
        string fullPath,
        IScanCoordinator scanCoordinator)
    {
        DirId = dirId;
        _name = name;
        _fullPath = fullPath;
        _scanCoordinator = scanCoordinator;
    }

    public Guid DirId { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (value == _name) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public string FullPath
    {
        get => _fullPath;
        set
        {
            if (value == _fullPath) return;
            _fullPath = value;
            OnPropertyChanged();
        }
    }

    public bool ShowFullPath
    {
        get => _showFullPath;
        set
        {
            if (SetProperty(ref _showFullPath, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    public FolderNodeViewModel? Parent { get; set; }

    public string DisplayName => ShowFullPath ? FullPath : Name;

    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    public bool IsScanRoot => Parent == null;

    // A callback that the owning viewmodel can set to remove this node
    public Action<FolderNodeViewModel>? OnRootRemoved { get; set; }

    [RelayCommand]
    private async Task QuickRescanAsync()
    {
        await _scanCoordinator.RunScanWithDialogAsync(FullPath, ScanMode.Quick);
    }

    [RelayCommand]
    private async Task FullRescanAsync()
    {
        await _scanCoordinator.RunScanWithDialogAsync(FullPath);
    }

    [RelayCommand]
    private async Task RemoveRootAsync()
    {
        if (!IsScanRoot)
            return;

        await _scanCoordinator.RemoveScanRootAsync(FullPath);
        OnRootRemoved?.Invoke(this);
    }

    public override string ToString()
    {
        return FullPath;
    }
}