using System.Threading.Tasks;

using Avalonia.Controls;

using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Features.Duplicates.Views;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Ui.Smoke;

[Collection("AvaloniaUI")]
public sealed class DuplicatesViewSmokeTests(AvaloniaHeadlessFixture ui)
{
    [Fact]
    public async Task DuplicatesView_HasKeyControls_ByName()
    {
        await ui.RunOnUiThreadAsync(() =>
        {
            var repo = new FakeRepo([]);
            var host = new FakeRepoHost(repo);
            var scan = new FakeScanCoordinator();
            var dialogs = new FakeDialogService();
            var deleter = new FakeFileSystemDeleteService();

            var vm = new DuplicatesViewModel(host, scan, dialogs, deleter);

            var view = new DuplicatesView
            {
                DataContext = vm
            };

            LayoutTestHelpers.DoLayout(view);

            Assert.NotNull(view.FindControl<Control>("ScanRootsHost"));
            Assert.NotNull(view.FindControl<Control>("ScanRootsTree"));
            Assert.NotNull(view.FindControl<Control>("MainTabs"));
            Assert.NotNull(view.FindControl<Control>("DuplicateSetsRepeater"));
            Assert.NotNull(view.FindControl<Control>("DuplicateFilesGrid"));
            Assert.NotNull(view.FindControl<Control>("TreeMap"));
        });
    }
}
