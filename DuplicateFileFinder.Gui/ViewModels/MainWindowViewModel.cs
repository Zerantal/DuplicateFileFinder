using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateFileFinder.Gui.Models;
using DuplicateFileFinder.Gui.Services;
using DuplicateFileFinder.Gui.Util;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Logging;
using NLog;


// for Dispatcher.UIThread

namespace DuplicateFileFinder.Gui.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    
    private readonly IFolderPickerService _folderPicker;
    private readonly IFilePickerService _filePicker;


    // ---------- Observable collections ----------
    // Folders the user wants to scan
    public ObservableCollection<string> SearchPaths { get; } = [];

    // The table of duplicate file records shown in the grid
    public DataGridCollectionView DuplicateFilesView { get; }

    private BulkObservableCollection<DuplicateFileModel> DuplicateFiles { get; } = [];

    private static readonly string[] Filters = ["csv"];

    // Observable properties
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanLocationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewScanCommand))]
    private bool _isScanning;

    [ObservableProperty] private bool _readyToScan;
    [ObservableProperty] private int _scanProgress;
    [ObservableProperty] private string _operation = string.Empty;
    [ObservableProperty] private int _filesScanned;
    [ObservableProperty] private int _duplicatesFound;
    [ObservableProperty] private long _spaceTaken;
    [ObservableProperty] private bool _isIndeterminateProgressPhase;

    // cancellation source for the current scan (null when idle)
    private CancellationTokenSource? _scanCts;
    private readonly DuplicateFileFinderLib.Core.DuplicateFileFinder _engine = new();

    // guard to prevent file scanning updating UI after it's finished
    private bool _finalized;

    public MainWindowViewModel(IFolderPickerService folderPicker, IFilePickerService filePicker)
    {
        _folderPicker = folderPicker;
        _filePicker = filePicker;

        ReadyToScan = false;
        IsScanning = false;

        DuplicateFilesView = new DataGridCollectionView(DuplicateFiles);

        // Default sort: FileSize DESC
        if (DuplicateFilesView.CanSort)
            DuplicateFilesView.SortDescriptions.Add(
                DataGridSortDescription.FromPath("FileSize", ListSortDirection.Descending));

        SearchPaths.CollectionChanged += (sender, _) =>
        {
            if (sender is ObservableCollection<string> paths) ReadyToScan = paths.Count > 0 && !IsScanning;
        };
    }

    public bool CanStartScan => !IsScanning;
    public bool CanImportExport => !IsScanning;

    // ---------------- Commands ----------------

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task ScanLocation()
    {
        var path = await _folderPicker.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!SearchPaths.Contains(path))
            SearchPaths.Add(path);

        using (ScanLog.BeginScanScope(path))
        {
            await StartScan(path);
        }
    }


    [RelayCommand(CanExecute = nameof(CanImportExport))]
    private async Task SaveScan()
    {
        // No data yet
        if (DuplicateFiles.Count == 0 && _engine.TotalFilesScanned == 0)
            return;

        var targetPath = await _filePicker.PickSaveFileAsync(
            "duplicate-scan.csv",
            [("CSV files", Filters)]);

        if (string.IsNullOrWhiteSpace(targetPath)) return;

        try
        {
            await using var fs = File.Create(targetPath);
            await using var sw = new StreamWriter(fs, new UTF8Encoding(false));
            _engine.ExportToCsv(sw); // relies on your existing lib API
            Operation = $"Exported scan to {targetPath}";
        }
        catch (Exception ex)
        {
            Operation = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportExport))]
    private async Task OpenScan()
    {
        var srcPath = await _filePicker.PickOpenFileAsync(
            [("CSV files", Filters)]);
        if (string.IsNullOrWhiteSpace(srcPath)) return;

        try
        {
            await using var fs = File.OpenRead(srcPath);
            using var sr = new StreamReader(fs, Encoding.UTF8, true);

            // Clear current state
            SearchPaths.Clear();
            FilesScanned = 0;
            DuplicatesFound = 0;
            SpaceTaken = 0;

            // Populate engine from CSV then materialize grid rows
            _engine.ImportFromCsv(sr, ImportMode.Replace);

            var rows = await _engine.GetDuplicateFileRowsAsync();
            var items = rows.Select(r => new DuplicateFileModel
            {
                FileName = r.Path,
                FileSize = r.Size,
                CreationDate = r.CreationTimeUtc.ToLocalTime(),
                Folder = r.Folder,
                FileGroup = r.Group
            }).ToList();
            
            DuplicateFiles.AddRange(items, true);

            foreach (var item in _engine.SearchPaths) SearchPaths.Add(item);

            FilesScanned = _engine.TotalFilesScanned;
            DuplicatesFound = _engine.DuplicateFilesWastedCount;
            SpaceTaken = _engine.DuplicateSpaceBytes;

            Operation = $"Imported scan from {srcPath}";
        }
        catch (Exception ex)
        {
            Operation = $"Import failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportExport))]
    private void NewScan()
    {
        _engine.ClearAllScans();
        SearchPaths.Clear();
        DuplicateFiles.Clear();
        FilesScanned = 0;
        DuplicatesFound = 0;
        SpaceTaken = 0;
        Operation = "Cleared results";
    }

    // ---------------- Private helper methods ----------------
    private async Task StartScan(string path)
    {
        var scanInterrupted = false;
        
        Log.Info("Initialising scan of {path}", path);
        
        if (IsScanning || SearchPaths.Count == 0) return;

        _finalized = false;

        // Reset UI state            
        ScanProgress = 0;
        Operation = "Preparing scan...";
        ReadyToScan = false;
        IsScanning = true;

        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        // Progress from the library → UI
        var progress = new Progress<DuplicateFileFinderProgressReport>(report =>
        {
            if (_finalized) return;

            if (!string.IsNullOrWhiteSpace(report.StatusMessage))
                Operation = report.StatusMessage;

            IsIndeterminateProgressPhase = report.IsIndeterminate;

            if (!report.IsIndeterminate)
                ScanProgress = (int)Math.Clamp(report.PercentComplete * 100.0, 0, 100);
        });

        try
        {
            await Task.Run(async () =>
            {
                await _engine.ScanLocation(path, progress, token)
                    .ConfigureAwait(false);
            }, token).ConfigureAwait(true);

            // Gather duplicates as GUI rows
            var rows = await Task.Run(async () =>
                await _engine.GetDuplicateFileRowsAsync().ConfigureAwait(false), token).ConfigureAwait(true);

            var items = rows.Select(r => new DuplicateFileModel
            {
                FileName = r.Path,
                FileSize = r.Size,
                CreationDate = r.CreationTimeUtc.ToLocalTime(),
                Folder = r.Folder,
                FileGroup = r.Group
            }).ToList();


            DuplicateFiles.AddRange(items, true);

            FilesScanned = _engine.TotalFilesScanned;
            DuplicatesFound = _engine.DuplicateFilesWastedCount;
            SpaceTaken = _engine.DuplicateSpaceBytes;

            ReadyToScan = SearchPaths.Count > 0;

            // refresh SearchPaths in case _engine has promoted prior scanned search paths
            SearchPaths.Clear();
            foreach (var item in _engine.SearchPaths) SearchPaths.Add(item);
        }
        catch (OperationCanceledException)
        {
            Operation = "Scan cancelled.";
            scanInterrupted = true;
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;


            _finalized = true;
            IsScanning = false;
            ReadyToScan = SearchPaths.Count > 0;
            ScanProgress = 0;
            IsIndeterminateProgressPhase = false;
            if (!scanInterrupted) Operation = "Finished scanning";
        }
    }

    [RelayCommand]
    private void StopScan()
    {
        _scanCts?.Cancel();
    }
}