// Gui/Services/IScanCoordinator.cs

using DuplicateFileFinderLib.Core;

namespace DuplicateFileFinder.Gui.Infrastructure.Services;

public sealed class ScanCompletedEventArgs(string path, bool cancelled, Exception? error) : EventArgs
{
    public string Path { get; } = path;
    public bool Cancelled { get; } = cancelled;
    public Exception? Error { get; } = error;
}

public interface IScanCoordinator
{
    bool IsScanning { get; }
    public event EventHandler<DuplicateFileFinderProgressReport>? ProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    public Task RunScanWithDialogAsync(
        string rootPath,
        CancellationToken cancellationToken = default);


    public Task RemoveScanRoot(long scanRootId);

    public void CancelScan();
}