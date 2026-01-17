// DuplicateFileFinder.GuiTests/UI/Smoke/DuplicatesViewSmokeTests.cs

using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.VisualTree;

using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTreeFlat;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.Views;
using DuplicateFileFinder.Gui.Features.Duplicates.Views.DuplicateGroups;
using DuplicateFileFinder.Gui.Infrastructure.Util;

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

            using var disposer = new DisposableManager();

            // Assemble graph (DI-style)
            var dupController = new DuplicateGroupsController(host);
            var fakeDeletionService = new FakeDuplicateFileDeletionService();
            var duplicateGroupsVm = new DuplicateGroupsViewModel(dupController, fakeDeletionService);

            // ---- ScanRoots tree: builder + actions + factory + vm ----
            var scanRootsBuilder = new ScanRootsTreeBuilder(host);

            var scanRootsActions = new ScanRootsTreeNodeActions(
                host: host,
                scanner: scan,
                dialogs: dialogs,
                deleter: deleter);

            var scanRootsVm = new ScanRootsFlatTreeViewModel(
                builder: scanRootsBuilder,
                actions: scanRootsActions);

            // ---- TreeMap ----
            var treeMapController = new TreeMapController(host)
            {
                Options = new TreeMapBuildOptions { MaxDepth = 8 }
            };

            var treeMapActionsVm = new TreeMapActionsViewModel(host, scan, dialogs, deleter, disposer);

            var vm = new DuplicatesViewModel(
                host,
                scanRootsVm,
                treeMapController,
                treeMapActionsVm,
                duplicateGroupsVm);

            var view = new DuplicatesView { DataContext = vm };

            // Put in a TopLevel so templates/styles/materialization happen.
            var window = new Window { Content = view };
            LayoutTestHelpers.DoLayout(window);

            // Controls that are directly in DuplicatesView
            Assert.NotNull(view.FindControl<Control>("ScanRootsHost"));
            Assert.NotNull(view.FindControl<Control>("ScanRootsTree"));
            Assert.NotNull(view.FindControl<Control>("MainTabs"));
            Assert.NotNull(view.FindControl<Control>("TreeMap"));

            // ---- DuplicateGroups controls: resolve the subview, then search within it ----
            var groupsView = window.GetVisualDescendants()
                .OfType<DuplicateGroupsView>()
                .FirstOrDefault();

            Assert.NotNull(groupsView);

            Assert.NotNull(groupsView.FindControl<Control>("DuplicateSetsRepeater"));
            Assert.NotNull(groupsView.FindControl<Control>("DuplicateFilesGrid"));
        });
    }
}
