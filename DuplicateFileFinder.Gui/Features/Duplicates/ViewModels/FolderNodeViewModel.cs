// ViewModels/FolderNodeViewModel.cs

using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Infrastructure.Services;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;

public sealed partial class FolderNodeViewModel : ObservableObject
{
    private readonly IScanCoordinator? _scanCoordinator;
    private readonly IDialogService? _dialogs;
    private readonly bool _isDummy;

    private string _fullPath;
    private string _name;
    private long _scanRootId = -1;

    // Dummy child used to show the expand arrow before children are loaded.
    private static readonly FolderNodeViewModel _dummyChild =
        new(0, string.Empty, string.Empty, null, null, isDummy: true);

    public FolderNodeViewModel(
        long dirId,
        string name,
        string fullPath,
        IScanCoordinator scanCoordinator,
        IDialogService dialogs,
        long scanRootId = -1)
        : this(dirId, name, fullPath, scanCoordinator, dialogs, isDummy: false, scanRootId: scanRootId)
    {
    }

    // Back-compat constructor (existing call sites)
    public FolderNodeViewModel(
        long dirId,
        string name,
        string fullPath,
        IScanCoordinator scanCoordinator,
        long scanRootId = -1)
        : this(dirId, name, fullPath, scanCoordinator, dialogs: null, isDummy: false, scanRootId: scanRootId)
    {
    }

    private FolderNodeViewModel(
        long dirId,
        string name,
        string fullPath,
        IScanCoordinator? scanCoordinator,
        IDialogService? dialogs,
        bool isDummy,
        long scanRootId = -1)
    {
        DirId = dirId;
        _name = name;
        _fullPath = fullPath;
        _scanCoordinator = scanCoordinator;
        _dialogs = dialogs;
        ScanRootId = scanRootId;
        _isDummy = isDummy;
    }

    public long DirId { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (value == _name)
                return;
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string FullPath
    {
        get => _fullPath;
        set
        {
            if (value == _fullPath)
                return;
            _fullPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public bool ShowFullPath
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    public FolderNodeViewModel? Parent { get; set; }

    public string DisplayName => ShowFullPath ? FullPath : Name;

    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    public bool IsScanRoot => Parent == null;

    // A callback that the owning viewmodel can set to remove this node
    public Action<FolderNodeViewModel>? OnRootRemoved { get; set; }

    // Called after updating scan root metadata so the owner can rebuild the tree labels
    public Action? OnRootLabelRefreshRequested { get; set; }

    public Action<FolderNodeViewModel>? EnsureChildrenLoaded { get; init; }

    public bool IsExpanded
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
                return;

            if (field)
                EnsureChildrenLoaded?.Invoke(this);
        }
    }

    internal void AddDummyChild()
    {
        Children.Clear();
        Children.Add(_dummyChild);
    }

    internal bool HasDummyChild =>
        Children.Count == 1 && ReferenceEquals(Children[0], _dummyChild);

    public long ScanRootId
    {
        get => _scanRootId;
        set => _scanRootId = value;
    }

    internal void ClearChildren() => Children.Clear();

    [RelayCommand]
    private async Task FullRescanAsync()
    {
        if (_isDummy || _scanCoordinator is null)
            return;

        await _scanCoordinator.RunScanWithDialogAsync(FullPath);
    }

    [RelayCommand]
    private async Task RemoveRootAsync()
    {
        if (_isDummy || ScanRootId < 0 || _scanCoordinator is null)
            return;

        if (!IsScanRoot)
            return;

        await _scanCoordinator.RemoveScanRoot(ScanRootId);
        OnRootRemoved?.Invoke(this);
    }

    [RelayCommand]
    private async Task SetDisplayNameAsync()
    {
        if (_isDummy || ScanRootId < 0 || _scanCoordinator is null || _dialogs is null)
            return;

        if (!IsScanRoot)
            return;

        var input = await _dialogs.ShowTextInputAsync(
            title: "Set display name",
            message: "Enter a display name for this scan root (blank = clear).",
            initialText: null);

        if (input is null)
            return; // cancelled

        var trimmed = input.Trim();
        var newName = trimmed.Length == 0 ? null : trimmed;

        await _scanCoordinator.SetScanRootDisplayName(ScanRootId, newName);

        // Rebuild labels from ScanRootsView (to apply VolumeLabel/path formatting too)
        OnRootLabelRefreshRequested?.Invoke();
    }

    [RelayCommand]
    private async Task ClearDisplayNameAsync()
    {
        if (_isDummy || ScanRootId < 0 || _scanCoordinator is null)
            return;

        if (!IsScanRoot)
            return;

        await _scanCoordinator.SetScanRootDisplayName(ScanRootId, null);
        OnRootLabelRefreshRequested?.Invoke();
    }

    public override string ToString() => FullPath;
}
