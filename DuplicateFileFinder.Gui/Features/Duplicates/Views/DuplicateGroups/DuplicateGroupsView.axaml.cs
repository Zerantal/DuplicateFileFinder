// Features/Controller/Views/Controller/DuplicateGroupsView.axaml.cs

using Avalonia.Controls;
using Avalonia.Input;

using DuplicateFileFinder.Gui.Features.Duplicates.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Views.DuplicateGroups;

public partial class DuplicateGroupsView : UserControl
{
    // Rough estimate (px) of one row in the ItemsRepeater. Used only for paging prefetch.
    private const double EstimatedRowHeight = 24;

    public DuplicateGroupsView()
    {
        InitializeComponent();

        // Trigger background page fetches as the user scrolls.
        if (DuplicateSetsScroll is not null)
            DuplicateSetsScroll.ScrollChanged += (_, _) => MaybeRequestNextPage();
    }

    private void MaybeRequestNextPage()
    {
        if (DataContext is not ViewModels.DuplicateGroups.DuplicateGroupsViewModel vm)
            return;

        if (DuplicateSetsScroll is null)
            return;

        var lastVisible = (int)((DuplicateSetsScroll.Offset.Y + DuplicateSetsScroll.Viewport.Height) / EstimatedRowHeight);

        // Prefetch ahead a bit so we don’t stall on a hard boundary.
        vm.OnNearEnd(lastVisible + 20);
    }

    private void OnDuplicateSetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ViewModels.DuplicateGroups.DuplicateGroupsViewModel vm)
            return;

        if (sender is not Control c)
            return;

        if (c.DataContext is not DuplicateSetRow row)
            return;

        vm.SelectedSet = row;
    }
}
