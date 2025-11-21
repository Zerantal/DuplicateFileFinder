// Gui/ViewModels/ScanProgressViewModel.cs
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateFileFinder.Gui.Services;
using DuplicateFileFinderLib.Core;

namespace DuplicateFileFinder.Gui.ViewModels;

public partial class ScanProgressViewModel : ObservableObject
{
    private readonly IScanCoordinator _scanCoordinator;

    [ObservableProperty] private string _phaseText = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _displayStatusMessage = string.Empty;

    [ObservableProperty] private int _scanProgress;
    [ObservableProperty] private bool _isIndeterminate = true;

    public ScanProgressViewModel(IScanCoordinator scanCoordinator)
    {
        _scanCoordinator = scanCoordinator ?? throw new ArgumentNullException(nameof(scanCoordinator));
    }

    public void Update(DuplicateFileFinderProgressReport report)
    {
        PhaseText = MapPhase(report.Phase);

        StatusMessage = report.StatusMessage ?? string.Empty;
        DisplayStatusMessage = StatusMessage;

        IsIndeterminate = report.IsIndeterminate;

        if (!report.IsIndeterminate)
        {
            var pct = (int)Math.Clamp(report.PercentComplete * 100.0, 0, 100);
            ScanProgress = pct;
        }
    }

    private static string MapPhase(ScanPhase phase) => phase switch
    {
        ScanPhase.Preparing            => "Preparing",
        ScanPhase.Enumerating          => "Enumerating files",
        ScanPhase.Hashing              => "Hashing files",
        ScanPhase.Grouping             => "Grouping duplicates",
        ScanPhase.Committing           => "Committing changes",
        ScanPhase.RecomputingAggregates => "Recomputing aggregates",
        ScanPhase.Completed            => "Completed",
        _                              => phase.ToString()
    };
    
    [RelayCommand]
    private void Cancel()
    {
        _scanCoordinator.CancelScan();
    }
}
