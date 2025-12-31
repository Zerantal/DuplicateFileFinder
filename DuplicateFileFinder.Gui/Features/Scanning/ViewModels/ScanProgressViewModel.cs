// Gui/ViewModels/ScanProgressViewModel.cs

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinderLib.Core;

namespace DuplicateFileFinder.Gui.Features.Scanning.ViewModels;

public partial class ScanProgressViewModel : ObservableObject
{
    private readonly IScanCoordinator _coordinator;

    [ObservableProperty] private bool _isCancelEnabled = true;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private string _phaseText = string.Empty;
    [ObservableProperty] private int _scanProgress;
    [ObservableProperty] private string _statusMessage = string.Empty;


    public ScanProgressViewModel(IScanCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public bool CanCancel => IsCancelEnabled;

    public void Update(DuplicateFileFinderProgressReport report)
    {
        PhaseText = MapPhase(report.Phase);
        StatusMessage = report.StatusMessage;
        IsIndeterminate = report.IsIndeterminate;

        if (!report.IsIndeterminate)
        {
            var pct = (int)Math.Clamp(report.PercentComplete * 100.0, 0, 100);
            ScanProgress = pct;
        }
    }

    private static string MapPhase(ScanPhase phase)
    {
        return phase switch
        {
            ScanPhase.Preparing => "Preparing",
            ScanPhase.Enumerating => "Enumerating files",
            ScanPhase.Hashing => "Hashing files",
            ScanPhase.Completed => "Completed",
            _ => phase.ToString()
        };
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsCancelEnabled = false;
        _coordinator.CancelScan();
    }
}