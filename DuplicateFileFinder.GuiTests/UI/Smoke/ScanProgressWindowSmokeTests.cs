using System;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using DuplicateFileFinder.Gui.Features.Scanning.ViewModels;
using DuplicateFileFinder.Gui.Features.Scanning.Views;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using DuplicateFileFinderLib.Core;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Ui.Smoke;

public sealed class ScanProgressWindowSmokeTests
{
    [AvaloniaFact]
    public Task ScanProgressWindow_BindsProgressAndText()
    {
        try
        {
            var scan = new FakeScanCoordinator();
            var vm = new ScanProgressViewModel(scan);

            vm.Update(new DuplicateFileFinderProgressReport
            {
                Phase = ScanPhase.Hashing,
                StatusMessage = "Hashing 123 files",
                IsIndeterminate = false,
                PercentComplete = 0.42
            });

            var win = new ScanProgressWindow { DataContext = vm };

            LayoutTestHelpers.DoLayout(win);

            var phase = win.FindControl<TextBlock>("PhaseText");
            var status = win.FindControl<TextBlock>("StatusText");
            var bar = win.FindControl<ProgressBar>("ScanProgressBar");
            var cancel = win.FindControl<Button>("CancelButton");

            Assert.Equal("Hashing files", phase!.Text);
            Assert.Equal("Hashing 123 files", status!.Text);
            Assert.Equal(42, (int)bar!.Value);
            Assert.NotNull(cancel);
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }
}
