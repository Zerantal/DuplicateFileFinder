// ViewModels/MainWindowViewModel.cs

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateFileFinder.Gui.Services;
using DuplicateFileFinderLib.Repository;
using NLog;

namespace DuplicateFileFinder.Gui.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IDialogService _dialogService;

    private readonly IScanCoordinator _scanCoordinator;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(OptimizeRepoCommand))]
    private bool _isScanning;

    /// <inheritdoc/>
    public MainWindowViewModel(Repo repo,
        IScanCoordinator scanCoordinator,
        IDialogService dialogService)
    {
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _scanCoordinator = scanCoordinator ?? throw new ArgumentNullException(nameof(scanCoordinator));
        Duplicates = new DuplicatesViewModel(repo, scanCoordinator);
        
        _scanCoordinator.ScanCompleted += (_, _) =>
        {
            IsScanning = false;
            Duplicates.LoadFromRepo();
        };
    }

    public DuplicatesViewModel Duplicates { get; }

    public bool CanStartScan => !IsScanning && !_scanCoordinator.IsScanning;

    // ---------------- Commands ----------------

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task ScanLocationAsync()
    {
        var path = await _dialogService.ShowFolderPickerDialogAsync("Scan location...");
        if (string.IsNullOrWhiteSpace(path))
            return;

        await StartScan(path);
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task OptimizeRepo()
    {
        try
        {
            await Duplicates.OptimizeRepoAsync();
            await _dialogService.ShowInfoAsync("Repository optimized", "The repository has been compacted.");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Failed to optimize repository", ex.Message);
        }
    }

    // ---------------- Scan orchestration ----------------

    private async Task StartScan(string path)
    {
        if (IsScanning || _scanCoordinator.IsScanning)
            return;

        Log.Info("Initialising scan of {path}", path);
        IsScanning = true;
        
        await _scanCoordinator.RunScanWithDialogAsync(path);
    }
}