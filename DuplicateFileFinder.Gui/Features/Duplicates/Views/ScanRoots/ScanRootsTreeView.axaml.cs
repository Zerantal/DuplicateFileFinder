using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Views.ScanRoots;

public partial class ScanRootsTreeView : UserControl
{
    private ScanRootsTreeViewModel? _vm;

    public ScanRootsTreeView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => HookVm();
        AttachedToVisualTree += (_, _) => HookVm();
        DetachedFromVisualTree += (_, _) => UnhookVm();
    }

    private void HookVm()
    {
        UnhookVm();

        _vm = DataContext as ScanRootsTreeViewModel;
        if (_vm is null)
            return;

        _vm.RequestCenterSelectedRow += VmOnRequestCenterSelectedRow;
    }

    private void UnhookVm()
    {
        if (_vm is not null)
            _vm.RequestCenterSelectedRow -= VmOnRequestCenterSelectedRow;

        _vm = null;
    }

    private void VmOnRequestCenterSelectedRow() =>
        Dispatcher.UIThread.Post(CenterSelectedRow, DispatcherPriority.Background);


    private void Row_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only left-click selects; right-click should keep working for context menu.
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (DataContext is not ScanRootsTreeViewModel vm)
            return;

        if (sender is not Control c || c.DataContext is not ScanRootsRowViewModel row)
            return;

        vm.SelectedRow = row;
        e.Handled = true;
    }

    private void CenterSelectedRow()
    {
        if (_vm?.SelectedRow is null)
            return;

        var scroller = this.FindControl<ScrollViewer>("PART_Scroller");
        var repeater = this.FindControl<ItemsRepeater>("PART_Repeater");
        if (scroller is null || repeater is null)
            return;

        var idx = _vm.Rows.IndexOf(_vm.SelectedRow);
        if (idx < 0)
            return;

        // Try to get the realized element first (best case)
        if (repeater.TryGetElement(idx) is { } realized)
        {
            CenterRealizedControl(scroller, realized);
            return;
        }

        // Not realized: coarse scroll using estimated row height, then retry.
        var rowHeight = TryGetAnyRealizedRowHeight(repeater) ?? 22.0;

        var viewportHeight = scroller.Viewport.Height;
        var extentHeight = scroller.Extent.Height;

        var desiredOffsetY =
            idx * rowHeight - viewportHeight / 2 + rowHeight / 2;

        var maxOffsetY = Math.Max(0, extentHeight - viewportHeight);
        desiredOffsetY = Math.Clamp(desiredOffsetY, 0, maxOffsetY);

        scroller.Offset = new Vector(scroller.Offset.X, desiredOffsetY);

        // After scrolling, the row is likely realized. Retry for precise centering.
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm?.SelectedRow is null)
                return;

            var idx2 = _vm.Rows.IndexOf(_vm.SelectedRow);
            if (idx2 < 0)
                return;

            if (repeater.TryGetElement(idx2) is { } realized2)
                CenterRealizedControl(scroller, realized2);
        }, DispatcherPriority.Background);
    }

    private static void CenterRealizedControl(ScrollViewer scroller, Control rowControl)
    {
        var topLeft = rowControl.TranslatePoint(new Point(0, 0), scroller);
        if (topLeft is null)
            return;

        var rowCenterYInScroller = topLeft.Value.Y + rowControl.Bounds.Height / 2;

        var viewportHeight = scroller.Viewport.Height;
        var extentHeight = scroller.Extent.Height;

        var desiredOffsetY =
            scroller.Offset.Y + rowCenterYInScroller - viewportHeight / 2;

        var maxOffsetY = Math.Max(0, extentHeight - viewportHeight);
        desiredOffsetY = Math.Clamp(desiredOffsetY, 0, maxOffsetY);

        scroller.Offset = new Vector(scroller.Offset.X, desiredOffsetY);
    }

    private static double? TryGetAnyRealizedRowHeight(ItemsRepeater repeater)
    {
        // Any realized element will do as a height estimate.
        // ItemsRepeater realizes only visible range, so this is cheap.
        foreach (var v in repeater.GetVisualChildren())
            if (v is Control c && c.Bounds.Height > 0)
                return c.Bounds.Height;

        return null;
    }
}
