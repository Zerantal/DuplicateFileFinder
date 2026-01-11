// ViewModels/FolderNodeViewModel.cs

using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

public sealed partial class FolderNodeViewModel(
    DirHandle dir,
    string name,
    string fullPath,
    IScanCoordinator? scanCoordinator,
    IDialogService? dialogs,
    IFileSystemDeleteService? deleter,
    IRepo? repo,
    long scanRootId,
    bool isDummy = false
    )
    : ObservableObject
{
    // Dummy child used to show the expand arrow before children are loaded.
    private static readonly FolderNodeViewModel s_dummyChild = new(
        DirHandle.Invalid,
        "Loading...",
        "",
        null,
        null,
        null,
        null,
        -1,
        true);

    public DirHandle Dir { get; } = dir;

    public FolderNodeViewModel? Parent { get; set; }

    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    public bool IsScanRoot => Parent == null;

    public long ScanRootId { get; } = scanRootId;

    public Action<FolderNodeViewModel>? EnsureChildrenLoaded { get; init; }

    // A callback that the owning viewmodel can set to remove this node
    public Action<FolderNodeViewModel>? OnRootRemoved { get; set; }

    // Called after updating scan root metadata so the owner can rebuild the tree labels
    public Action? OnRootLabelRefreshRequested { get; set; }

    // ----- UI state -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _fullPath = fullPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _statusTag;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RescanOrResumeHeader))]
    private bool _hasCheckpoint;

    [ObservableProperty]
    private bool _isAvailable = true;

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

    // ----- Aggregate stats -----

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ItemCount))]
    private int _fileCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ItemCount))]
    private int _dirCount;

    [ObservableProperty]
    private long _duplicateFiles;

    [ObservableProperty]
    private long _duplicateBytes;

    [ObservableProperty]
    private double _percentOfScanRoot;

    [ObservableProperty]
    private long _scanRootTotalBytes;

    public int ItemCount => FileCount + DirCount;

    public void ApplyAggregateStats(DirAggregateStats stats, long scanRootTotalBytes)
    {
        TotalBytes = stats.TotalBytes;
        FileCount = stats.FileCount;
        DirCount = stats.DirCount;
        DuplicateFiles = stats.DuplicateFiles;
        DuplicateBytes = stats.DuplicateBytes;

        ScanRootTotalBytes = scanRootTotalBytes <= 0 ? 0 : scanRootTotalBytes;
        PercentOfScanRoot = ScanRootTotalBytes <= 0 ? 0.0 : TotalBytes * 100.0 / ScanRootTotalBytes;
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
        if (!IsScanRoot || isDummy || scanCoordinator is null)
            return;

        // will resume a scan if there's a checkpoint
        await scanCoordinator.RunRescanLocationWithDialogAsync(ScanRootId);
    }

    [RelayCommand]
    private async Task RescanFolderAsync()
    {
        if (isDummy || scanCoordinator is null)
            return;

        if (!Dir.IsValid)
            return;

        await scanCoordinator.RunFolderRescanWithDialogAsync(Dir);
    }

    [RelayCommand]
    private async Task RemoveLocationAsync()
    {
        if (!IsScanRoot || isDummy || scanCoordinator is null || dialogs is null)
            return;

        var ok = await (dialogs?.ShowConfirmationAsync(
            "Remove scan root",
            "Remove this scan root from the repository?",
            "Remove") ?? Task.FromResult(false));

        if (!ok)
            return;

        await scanCoordinator.RemoveScanRoot(ScanRootId);
        OnRootRemoved?.Invoke(this);
    }

    [RelayCommand]
    private async Task SetDisplayNameAsync()
    {
        if (!IsScanRoot || isDummy || scanCoordinator is null || dialogs is null)
            return;

        var input = await dialogs.ShowTextInputAsync(
            "Set display name",
            "Enter a display name for this scan root (blank = clear).",
            Name);

        if (input is null)
            return;

        var normalized = string.IsNullOrWhiteSpace(input) ? null : input;

        await scanCoordinator.SetScanRootDisplayName(ScanRootId, normalized);

        OnRootLabelRefreshRequested?.Invoke();
    }

    public bool CanDeleteFromDisk => !IsScanRoot && !isDummy;

    [RelayCommand(CanExecute = nameof(CanDeleteFromDisk))]
    private async Task DeleteFromDiskAsync()
    {
        if (IsScanRoot || isDummy || dialogs is null || deleter is null || scanCoordinator is null || repo is null)
            return;

        var ok = await dialogs.ShowConfirmationAsync(
            "Delete folder",
            $"Delete this folder from disk?\n\n{FullPath}",
            okText: "Delete",
            cancelText: "Cancel");

        if (!ok)
            return;

        var (deleted, err) = await deleter.DeleteDirectoryAsync(FullPath, recursive: true);
        if (!deleted)
        {
            await dialogs.ShowErrorAsync("Delete failed", err ?? "Unknown error.");
            return;
        }

        var result = await repo.DeleteDirAsync(Dir);
        if (!result.Success)
        {
            await dialogs.ShowErrorAsync(
                "Delete error",
                $"Deleting entry from repository failed: {result.Error}");
        }
    }
}
