// Gui/ViewModels/ScanProgressViewModel.cs

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Core;

namespace DuplicateFileFinder.Gui.Features.Scanning.ViewModels;

public partial class ScanProgressViewModel(IScanCoordinator coordinator) : ObservableObject
{
    private readonly IScanCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    [ObservableProperty] private bool _isCancelEnabled = true;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private string _phaseText = string.Empty;
    [ObservableProperty] private int _scanProgress;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // When true, cancel becomes "dismiss"
    [ObservableProperty] private bool _isFinalizing;

    // UI label for the cancel/dismiss button
    [ObservableProperty] private string _cancelButtonText = "Cancel";

    public event EventHandler? RequestDismiss;

    public bool CanCancel => IsCancelEnabled;

    public void Update(DuplicateFileFinderProgressReport report)
    {
        // If we are finalizing, ignore subsequent scan progress updates.
        if (IsFinalizing)
            return;

        PhaseText = MapPhase(report.Phase);
        StatusMessage = report.StatusMessage;
        IsIndeterminate = report.IsIndeterminate;

        if (!report.IsIndeterminate)
        {
            var pct = (int)Math.Clamp(report.PercentComplete * 100.0, 0, 100);
            ScanProgress = pct;
        }
    }

    public void EnterFinalizing()
    {
        if (IsFinalizing)
            return;

        IsFinalizing = true;

        // Keep the button enabled so it can dismiss the window.
        IsCancelEnabled = true;

        CancelButtonText = "Dismiss";
        PhaseText = "Finalizing";
        StatusMessage = "Updating indexes…";
        IsIndeterminate = true;
        ScanProgress = 0;
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
        // Finalization phase: cancel is a no-op for the scan; it just dismisses the dialog.
        if (IsFinalizing)
        {
            RequestDismiss?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Scanning phase: cancel the scan.
        IsCancelEnabled = false;
        _coordinator.CancelScan();
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnIsCancelEnabledChanged(bool value)
    {
        // Ensure CanExecute reevaluates when enabled changes.
        CancelCommand.NotifyCanExecuteChanged();
    }
}
