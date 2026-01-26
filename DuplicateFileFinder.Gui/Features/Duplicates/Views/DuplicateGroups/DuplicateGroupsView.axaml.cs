// Features/Controller/Views/Controller/DuplicateGroupsView.axaml.cs

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

using DuplicateFileFinder.Gui.Features.Duplicates.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Views.DuplicateGroups;

public partial class DuplicateGroupsView : UserControl
{
    // Fallback only (used until we can sample a realized row).
    private const double FallbackEstimatedRowHeight = 18;

    // Cached measured height of a realized row.
    private double _rowHeight = FallbackEstimatedRowHeight;

    public DuplicateGroupsView()
    {
        InitializeComponent();

        // Trigger background page fetches as the user scrolls.
        if (DuplicateSetsScroll is not null)
            DuplicateSetsScroll.ScrollChanged += (_, _) => MaybeRequestNextPage();

        // Keep the row-height estimate accurate as templates/styles change.
        // (This is cheap: we only sample the first realized element.)
        LayoutUpdated += (_, _) => TryUpdateRowHeightFromRepeater();
    }

    private void MaybeRequestNextPage()
    {
        if (DataContext is not ViewModels.DuplicateGroups.DuplicateGroupsViewModel vm)
            return;

        if (DuplicateSetsScroll is null)
            return;

        var rowHeight = _rowHeight > 1 ? _rowHeight : FallbackEstimatedRowHeight;

        var lastVisible =
            (int)((DuplicateSetsScroll.Offset.Y + DuplicateSetsScroll.Viewport.Height) / rowHeight);

        vm.OnNearEnd(lastVisible + 20);
    }

    private void TryUpdateRowHeightFromRepeater()
    {
        if (DuplicateSetsRepeater is null)
            return;

        // ItemsRepeater virtualization means we only have containers for realized items.
        // Use the first realized child as our row-height sample.
        var child = DuplicateSetsRepeater.GetVisualChildren().OfType<Control>().FirstOrDefault();
        if (child is null)
            return;

        var h = child.Bounds.Height;

        // Ignore bogus values (during initial layout passes, height may be 0).
        if (h > 1 && !double.IsNaN(h) && !double.IsInfinity(h))
            _rowHeight = h;
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
