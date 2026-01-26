using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTreeFlat;

public sealed partial class ScanRootsFlatRowViewModel : ObservableObject
{
    private readonly IScanRootsTreeNodeActions? _actions;

    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private bool _showFullPath;

    public ScanRootsFlatRowViewModel(
        ScanRootsTreeNode model,
        IScanRootsTreeNodeActions? actions,
        int depth)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _actions = actions;
        Depth = depth;
    }

    public ScanRootsTreeNode Model { get; }

    public DirHandle Dir => Model.Dir;
    public long ScanRootId => Model.ScanRootId;

    public int Depth { get; }

    public bool IsScanRoot => Model.IsScanRoot;

    public bool HasLazyChildren => Model.HasLazyChildren;

    public string DisplayName
    {
        get
        {
            var baseText = ShowFullPath ? FullPath : Name;
            return string.IsNullOrWhiteSpace(StatusTag) ? baseText : $"{baseText} {StatusTag}";
        }
    }

    public string RescanOrResumeHeader => HasCheckpoint ? "Resume scan" : "Rescan location";

    public Action<ScanRootsFlatRowViewModel>? OnRootRemoved { get; set; }

    // Projection
    public string Name => Model.Name;
    public string FullPath => Model.FullPath;
    public string? StatusTag => Model.StatusTag;
    public bool HasCheckpoint => Model.HasCheckpoint;
    public bool IsAvailable => Model.IsAvailable;

    public long TotalBytes => Model.TotalBytes;
    public int FileCount => Model.FileCount;
    public int DirCount => Model.DirCount;
    public int ItemCount => Model.ItemCount;
    public long DuplicateFiles => Model.DuplicateFiles;
    public long DuplicateBytes => Model.DuplicateBytes;
    public double PercentOfScanRoot => Model.PercentOfScanRoot;

    // Used by the flat controller to remove descendants when collapsing
    internal int SubtreeDepth => Depth;

    public bool CanDeleteFromDisk => !IsScanRoot;

    public void RefreshFromModel()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(StatusTag));
        OnPropertyChanged(nameof(HasCheckpoint));
        OnPropertyChanged(nameof(IsAvailable));

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(RescanOrResumeHeader));

        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(DirCount));
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(DuplicateFiles));
        OnPropertyChanged(nameof(DuplicateBytes));
        OnPropertyChanged(nameof(PercentOfScanRoot));
    }

    // ---- Commands (copied from FolderNodeViewModel semantics) ----

    [RelayCommand]
    private Task RescanLocationAsync()
    {
        if (!IsScanRoot || _actions is null)
            return Task.CompletedTask;

        return _actions.RescanScanRootAsync(ScanRootId);
    }

    [RelayCommand]
    private Task RescanFolderAsync()
    {
        if (_actions is null || !Dir.IsValid)
            return Task.CompletedTask;

        return _actions.RescanFolderAsync(Dir);
    }

    [RelayCommand]
    private async Task RemoveLocationAsync()
    {
        if (!IsScanRoot || _actions is null)
            return;

        if (await _actions.TryRemoveScanRootAsync(ScanRootId))
            OnRootRemoved?.Invoke(this);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteFromDisk))]
    private async Task DeleteFromDiskAsync()
    {
        if (IsScanRoot || _actions is null)
            return;

        await _actions.DeleteFolderAsync(Dir, FullPath);
    }

    [RelayCommand]
    private async Task SetDisplayNameAsync()
    {
        if (!IsScanRoot || _actions is null)
            return;

        await _actions.TrySetScanRootDisplayNameAsync(ScanRootId, Name);
    }
}
