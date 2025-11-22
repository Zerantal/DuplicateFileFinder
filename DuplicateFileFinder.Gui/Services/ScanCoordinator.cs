// Gui/Services/ScanCoordinator.cs

using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.Repository;

namespace DuplicateFileFinder.Gui.Services;

public sealed class ScanCoordinator : IScanCoordinator
{
    private readonly DuplicateFileFinderLib.Core.DuplicateFileFinder _scanner;
    private readonly Repo _repo;

    private CancellationTokenSource? _cts;

    public ScanCoordinator(Repo repo, DuplicateFileFinderLib.Core.DuplicateFileFinder scanner)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    }

    public bool IsScanning { get; private set; }

    public event EventHandler<DuplicateFileFinderProgressReport>? ProgressChanged;

    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    public async Task RunScanAsync(string path)
    {
        if (IsScanning)
            return;

        IsScanning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var progress = new Progress<DuplicateFileFinderProgressReport>(report =>
        {
            ProgressChanged?.Invoke(this, report);
        });

        Exception? error = null;
        var cancelled = false;

        try
        {
            await Task.Run(
                () => _scanner.ScanLocationAsync(path, progress, token),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            try
            {
                _repo.SaveSnapshot();
            }
            catch
            {
                // swallow
            }

            IsScanning = false;
            _cts?.Dispose();
            _cts = null;

            ScanCompleted?.Invoke(this, new ScanCompletedEventArgs(path, cancelled, error));
        }
    }

    public void CancelScan()
    {
        if (!IsScanning)
            return;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // no-op; cancellation is best-effort
        }
    }
}

public sealed class ScanCompletedEventArgs : EventArgs
{
    public ScanCompletedEventArgs(string path, bool cancelled, Exception? error)
    {
        Path = path;
        Cancelled = cancelled;
        Error = error;
    }

    public string Path { get; }
    public bool Cancelled { get; }
    public Exception? Error { get; }

    public bool Succeeded => !Cancelled && Error is null;
}
