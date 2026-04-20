// DuplicateFileFinder.GuiTests/UI/Smoke/DuplicatesViewSmokeTests.cs

using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

using DuplicateFileFinder.Gui.Features.Duplicates.Application;
using DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.DuplicateGroups;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.Views;
using DuplicateFileFinder.Gui.Features.Duplicates.Views.DuplicateGroups;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinder.Gui.Infrastructure.Util;
using DuplicateFileFinder.GuiTests.UI.Fakes;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Ui.Smoke;

public sealed class DuplicatesViewSmokeTests
{
    [AvaloniaFact]
    public void DuplicatesView_HasKeyControls_ByName()
    {
        var repo = new FakeRepo([]);
        var host = new FakeRepoHost(repo);
        var scan = new FakeScanCoordinator();
        var dialogs = new FakeDialogService();

        using var disposer = new DisposableManager();

        // Assemble graph (DI-style)
        var dupController = new DuplicateGroupsController(host);
        var fakeDeletionService = new FakeDeletionWorkflowService();
        var fakeClipboardService = new FakeClipboardService();
        var duplicateGroupsVm =
            new DuplicateGroupsViewModel(host, dupController, fakeDeletionService, fakeClipboardService);
        var repoEventRelay = new RepoUiEventRelayPlugin(new AvaloniaUiDispatcher());

        // ---- ScanRoots tree: builder + actions + factory + vm ----
        var scanRootsBuilder = new ScanRootsTreeBuilder(host);

        var scanRootsActions = new ScanRootsTreeNodeActions(
            host: host,
            scanner: scan,
            dialogs: dialogs,
            clipboard: fakeClipboardService);

        var scanRootsVm = new ScanRootsTreeViewModel(
            repoEvents: repoEventRelay,
            builder: scanRootsBuilder,
            actions: scanRootsActions,
            deletionService: fakeDeletionService,
            disposer: new DisposableManager(),
            selectionContext: new DuplicateExplorerSelectionContext());

        // ---- TreeMap ----
        var treeMapController = new TreeMapController(
            host,
            new DuplicateExplorerSelectionContext(),
            new DisposableManager())
        {
            Options = new TreeMapBuildOptions { MaxDepth = 8 }
        };

        var treeMapActionsVm = new TreeMapActionsViewModel(
            host,
            scan,
            fakeDeletionService,
            new FakeClipboardService(),
            disposer);

        var vm = new DuplicatesViewModel(
            host,
            scanRootsVm,
            treeMapController,
            treeMapActionsVm,
            duplicateGroupsVm,
            new DuplicateExplorerSelectionContext(),
            new DisposableManager());

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
    }
}
