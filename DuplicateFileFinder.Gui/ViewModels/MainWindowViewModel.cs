// ViewModels/MainWindowViewModel.cs

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateFileFinder.Gui.Services;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using NLog;

namespace DuplicateFileFinder.Gui.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private IRepoHost? _repoHost;
    
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    
    private readonly IDialogService _dialogService;

    private readonly IScanCoordinator _scanCoordinator;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(OptimizeRepoCommand))]
    private bool _isScanning;

    /// <inheritdoc/>
    private MainWindowViewModel(IRepoHost host,
        IScanCoordinator scanCoordinator,
        IDialogService dialogService)
    {
        _repoHost = host;
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _scanCoordinator = scanCoordinator ?? throw new ArgumentNullException(nameof(scanCoordinator));
        Duplicates = new DuplicatesViewModel(host, scanCoordinator);

        _scanCoordinator.ScanCompleted += (_, _) =>
        {
            IsScanning = false;
            Duplicates.LoadFromRepo();
        };
    }

    public DuplicatesViewModel? Duplicates { get; set; }

    public bool CanStartScan => !IsScanning && !(_scanCoordinator is { IsScanning: true });

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
            await Duplicates!.OptimizeRepoAsync();
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

    public static async Task<MainWindowViewModel?> CreateMainWindowAsync(string repoDir)
    {
        MainWindowViewModel? mainWindowVm;
        try
        {
            var host = await RepoHost.OpenAsync(repoDir);

            var repo = host.Repo;

            // // Integrity check still works the same
            // var issues = repo.ValidateIntegrity();
            // foreach (var issue in issues)
            //     Console.WriteLine(issue.ToString());

            var dialogService = new DialogService();
            var scanEngine = new DuplicateFileFinderLib.Core.DuplicateFileFinder(host);
            var scanCoordinator = new ScanCoordinator(repo, scanEngine, dialogService);
        
            mainWindowVm = new MainWindowViewModel(host, scanCoordinator, dialogService);

        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            throw;
        }
        
        // mainWindowVm responsible for disposing of RepoHost (called via MainWindow.OnClosed)
        return mainWindowVm;
    }

    public async ValueTask DisposeAsync()
    {
        if (_repoHost != null) await _repoHost.DisposeAsync();
    }
}