// ViewModels/FolderNodeViewModel.cs

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateFileFinder.Gui.Infrastructure.Services;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;

public sealed partial class FolderNodeViewModel : ObservableObject
{
    private readonly IScanCoordinator? _scanCoordinator;
    private readonly bool _isDummy;

    private string _fullPath;
    private string _name;
    private long _scanRootId = -1;

    // Dummy child used to show the expand arrow before children are loaded.
    private static readonly FolderNodeViewModel DummyChild =
        new(0, string.Empty, string.Empty, null, isDummy: true);

    public FolderNodeViewModel(
        long dirId,
        string name,
        string fullPath,
        IScanCoordinator scanCoordinator,
        long scanRootId = -1)
        : this(dirId, name, fullPath, scanCoordinator, isDummy: false, scanRootId: scanRootId)
    {
    }

    private FolderNodeViewModel(
        long dirId,
        string name,
        string fullPath,
        IScanCoordinator? scanCoordinator,
        bool isDummy,
        long scanRootId = -1)
    {
        DirId = dirId;
        _name = name;
        _fullPath = fullPath;
        _scanCoordinator = scanCoordinator;
        ScanRootId = scanRootId;
        _isDummy = isDummy;
    }

    public long DirId { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (value == _name) return;
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
            if (value == _fullPath) return;
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
    
    public Action<FolderNodeViewModel>? EnsureChildrenLoaded { get; set; }

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

    // NEW: used by the owner VM when building the tree
    internal void AddDummyChild()
    {
        Children.Clear();
        Children.Add(DummyChild);
    }

    internal bool HasDummyChild =>
        Children.Count == 1 && ReferenceEquals(Children[0], DummyChild);

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
        if (_isDummy ||  ScanRootId < 0 || _scanCoordinator is null)
            return;

        if (!IsScanRoot)
            return;
        
        await _scanCoordinator.RemoveScanRoot(ScanRootId);
        OnRootRemoved?.Invoke(this);
    }

    public override string ToString()
    {
        return FullPath;
    }
}
