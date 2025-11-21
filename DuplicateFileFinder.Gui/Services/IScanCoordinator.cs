// Gui/Services/IScanCoordinator.cs
using System.Threading;
using System.Threading.Tasks;
using DuplicateFileFinder.Gui.ViewModels;
using DuplicateFileFinderLib.Core;

namespace DuplicateFileFinder.Gui.Services;

public interface IScanCoordinator
{
    bool IsScanning { get; }
    public event EventHandler<DuplicateFileFinderProgressReport>? ProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;
    public Task RunScanAsync(string path);
    public void CancelScan();
}