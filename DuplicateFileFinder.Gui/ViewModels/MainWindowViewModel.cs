using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateFileFinder.Gui.Services;
using DuplicateFileFinder.Gui.Views;
using DuplicateFileFinderLib.Repository;
using NLog;
using Dff = DuplicateFileFinderLib.Core;

namespace DuplicateFileFinder.Gui.ViewModels;

public partial class MainWindowViewModel(
    Repo repo,
    IScanCoordinator scanCoordinator,
    IDialogService dialogService)
    : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly IDialogService _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

    private readonly IScanCoordinator _scanCoordinator = scanCoordinator ?? throw new ArgumentNullException(nameof(scanCoordinator));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(OptimizeRepoCommand))]
    private bool _isScanning;

    public DuplicatesViewModel Duplicates { get; } = new(repo);

    public bool CanStartScan => !IsScanning;

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
        if (IsScanning)
            return;

        Log.Info("Initialising scan of {path}", path);
        IsScanning = true;

        var progressVm = new ScanProgressViewModel(_scanCoordinator);
        var dialog = new ScanProgressWindow
        {
            DataContext = progressVm
        };

        void HandleProgress(object? _, Dff.DuplicateFileFinderProgressReport report)
        {
            progressVm.Update(report);
        }

        void HandleCompleted(object? _, ScanCompletedEventArgs e)
        {
            Duplicates.LoadFromRepo();
        }

        _scanCoordinator.ProgressChanged += HandleProgress;
        _scanCoordinator.ScanCompleted += HandleCompleted;

        var owner = _dialogService.GetOwnerWindow();
        var dialogTask = dialog.ShowDialog(owner);

        try
        {
            await _scanCoordinator.RunScanAsync(path);
        }
        finally
        {
            _scanCoordinator.ProgressChanged -= HandleProgress;
            _scanCoordinator.ScanCompleted -= HandleCompleted;

            dialog.Close();
            await dialogTask;

            IsScanning = false;
        }
    }
}