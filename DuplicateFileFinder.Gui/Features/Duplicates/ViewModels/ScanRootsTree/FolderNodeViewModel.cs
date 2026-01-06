// ViewModels/FolderNodeViewModel.cs

using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

public sealed partial class FolderNodeViewModel : ObservableObject
{
    private readonly IScanCoordinator? _scanCoordinator;
    private readonly IDialogService? _dialogs;
    private readonly bool _isDummy;

    private string _fullPath;
    private string _name;
    private long _scanRootId = -1;

    // Aggregate stats (from TreeIndexStats)
    private long _totalBytes;
    private int _fileCount;
    private int _dirCount;
    private long _duplicateFiles;
    private long _duplicateBytes;
    private double _percentOfScanRoot;
    private long _scanRootTotalBytes;

    // Dummy child used to show the expand arrow before children are loaded.
    private static readonly FolderNodeViewModel s_dummyChild =
        new(new DirHandle(), string.Empty, string.Empty, null, null, isDummy: true);

    public FolderNodeViewModel(
        DirHandle dir,
        string name,
        string fullPath,
        IScanCoordinator scanCoordinator,
        IDialogService dialogs,
        long scanRootId = -1)
        : this(dir, name, fullPath, scanCoordinator, dialogs, isDummy: false, scanRootId: scanRootId)
    {
    }

    private FolderNodeViewModel(
        DirHandle dir,
        string name,
        string fullPath,
        IScanCoordinator? scanCoordinator,
        IDialogService? dialogs,
        bool isDummy,
        long scanRootId = -1)
    {
        Dir = dir;
        _name = name;
        _fullPath = fullPath;
        _scanCoordinator = scanCoordinator;
        _dialogs = dialogs;
        ScanRootId = scanRootId;
        _isDummy = isDummy;
    }

    public DirHandle Dir { get; }

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
        Children.Add(s_dummyChild);
    }

    internal bool HasDummyChild =>
        Children.Count == 1 && ReferenceEquals(Children[0], s_dummyChild);

    public long ScanRootId
    {
        get => _scanRootId;
        set => _scanRootId = value;
    }

    internal void ClearChildren() => Children.Clear();

    // ---- Aggregate stats bindings (TreeIndexStats) ----

    public long TotalBytes
    {
        get => _totalBytes;
        private set => SetProperty(ref _totalBytes, value);
    }

    public int FileCount
    {
        get => _fileCount;
        private set => SetProperty(ref _fileCount, value);
    }

    public int DirCount
    {
        get => _dirCount;
        private set => SetProperty(ref _dirCount, value);
    }

    public int ItemCount => FileCount + DirCount;

    public long DuplicateFiles
    {
        get => _duplicateFiles;
        private set => SetProperty(ref _duplicateFiles, value);
    }

    public long DuplicateBytes
    {
        get => _duplicateBytes;
        private set => SetProperty(ref _duplicateBytes, value);
    }

    /// <summary>Percent of the scan-root total (WinDirStat-like “Subtree %”).</summary>
    public double PercentOfScanRoot
    {
        get => _percentOfScanRoot;
        private set => SetProperty(ref _percentOfScanRoot, value);
    }

    /// <summary>Total bytes of the scan root this node belongs to (used to compute PercentOfScanRoot).</summary>
    public long ScanRootTotalBytes
    {
        get => _scanRootTotalBytes;
        private set => SetProperty(ref _scanRootTotalBytes, value);
    }

    public void ApplyAggregateStats(DirAggregateStats stats, long scanRootTotalBytes)
    {
        TotalBytes = stats.TotalBytes;
        FileCount = stats.FileCount;
        DirCount = stats.DirCount;
        DuplicateFiles = stats.DuplicateFiles;
        DuplicateBytes = stats.DuplicateBytes;

        ScanRootTotalBytes = scanRootTotalBytes <= 0 ? 0 : scanRootTotalBytes;
        PercentOfScanRoot =
            ScanRootTotalBytes <= 0 ? 0.0 : TotalBytes * 100.0 / ScanRootTotalBytes;

        OnPropertyChanged(nameof(ItemCount));
    }

    // ---- Existing commands ----

    [RelayCommand]
    private async Task RescanLocationAsync()
    {
        if (!IsScanRoot || _isDummy || _scanCoordinator is null)
            return;

        await _scanCoordinator.RunRescanLocationWithDialogAsync(_scanRootId);
    }

    [RelayCommand]
    private async Task RescanFolderAsync()
    {
        if (_isDummy || _scanCoordinator is null)
            return;

        var handle = Dir;
        if (!handle.IsValid)
            return;

        await _scanCoordinator.RunFolderRescanWithDialogAsync(handle);
    }

    [RelayCommand]
    private async Task RemoveLocationAsync()
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
