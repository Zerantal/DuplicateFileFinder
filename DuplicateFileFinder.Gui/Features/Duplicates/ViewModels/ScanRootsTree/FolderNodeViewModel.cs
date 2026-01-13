// DuplicateFileFinder.Gui/Features/Duplicates/ViewModels/ScanRootsTree/FolderNodeViewModel.cs

using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

public sealed partial class FolderNodeViewModel : ObservableObject
{
    // Dummy child used to show the expand arrow before children are loaded.
    private static readonly FolderNodeViewModel s_dummyChild = CreateDummy();

    private readonly IScanRootsTreeNodeActions? _actions;
    private readonly bool _isDummy;

    public FolderNodeViewModel(
        ScanRootsTreeNode model,
    IScanRootsTreeNodeActions? actions,
    bool isDummy = false)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _actions = actions;
        _isDummy = isDummy;
    }

    private static FolderNodeViewModel CreateDummy()
        => new(new ScanRootsTreeNode
        {
            Dir = DirHandle.Invalid,
            Name = "Loading...",
            FullPath = "",
            ScanRootId = -1,
            IsScanRoot = false,
            ChildrenMaterialized = true,
            HasLazyChildren = false
        }, actions: null, isDummy: true);

    /// <summary>Application model this VM projects.</summary>
    public ScanRootsTreeNode Model { get; }

    public DirHandle Dir => Model.Dir;

    public long ScanRootId => Model.ScanRootId;

    public FolderNodeViewModel? Parent { get; set; }

    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    // View-level decision. Root nodes are created with Parent == null.
    public bool IsScanRoot => Parent is null;

    // Provided by factory/VM: triggers model materialization and populates Children VMs.
    public Action<FolderNodeViewModel>? EnsureChildrenLoaded { get; set; }

    // These two should go away long-term; keep only during transition.
    public Action<FolderNodeViewModel>? OnRootRemoved { get; set; }
    public Action? OnRootLabelRefreshRequested { get; set; }

    // ----- UI state -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private bool _showFullPath;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            EnsureChildrenLoaded?.Invoke(this);
    }

    public string DisplayName
    {
        get
        {
            var baseText = ShowFullPath ? FullPath : Name;
            return string.IsNullOrWhiteSpace(StatusTag) ? baseText : $"{baseText} {StatusTag}";
        }
    }

    public string RescanOrResumeHeader => HasCheckpoint ? "Resume scan" : "Rescan location";

    // ----- Projection properties (read from Model) -----

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
    public long ScanRootTotalBytes => Model.ScanRootTotalBytes;

    /// <summary>
    /// Call when the underlying model was updated and the UI needs to refresh bindings.
    /// (Common during rebuild or when display name/status tag changes.)
    /// </summary>
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
        OnPropertyChanged(nameof(ScanRootTotalBytes));
    }

    // ----- Dummy children helpers -----

    internal bool HasDummyChild =>
        Children.Count == 1 && ReferenceEquals(Children[0], s_dummyChild);

    internal void AddDummyChild()
    {
        Children.Clear();
        Children.Add(s_dummyChild);
    }

    internal void ClearChildren() => Children.Clear();

    // ----- Commands -----

    [RelayCommand]
    private async Task RescanLocationAsync()
    {
        if (!IsScanRoot || _isDummy || _actions is null)
            return;

        await _actions.RescanScanRootAsync(ScanRootId);
    }

    [RelayCommand]
    private async Task RescanFolderAsync()
    {
        if (_isDummy || _actions is null || !Dir.IsValid)
            return;

        await _actions.RescanFolderAsync(Dir);
    }

    [RelayCommand]
    private async Task RemoveLocationAsync()
    {
        if (!IsScanRoot || _isDummy || _actions is null)
            return;

        if (await _actions.TryRemoveScanRootAsync(ScanRootId))
            OnRootRemoved?.Invoke(this);
    }

    [RelayCommand]
    private async Task SetDisplayNameAsync()
    {
        if (!IsScanRoot || _isDummy || _actions is null)
            return;

        // This only changes repo state. Your rebuild path should refresh the model + call RefreshFromModel().
        if (await _actions.TrySetScanRootDisplayNameAsync(ScanRootId, Name))
            OnRootLabelRefreshRequested?.Invoke();
    }

    public bool CanDeleteFromDisk => !IsScanRoot && !_isDummy;

    [RelayCommand(CanExecute = nameof(CanDeleteFromDisk))]
    private Task DeleteFromDiskAsync()
    {
        if (IsScanRoot || _isDummy || _actions is null)
            return Task.CompletedTask;

        return _actions.DeleteFolderAsync(Dir, FullPath);
    }
}
