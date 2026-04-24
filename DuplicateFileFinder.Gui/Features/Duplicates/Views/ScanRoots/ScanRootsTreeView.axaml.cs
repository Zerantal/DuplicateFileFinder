using System.Collections.Specialized;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Views.ScanRoots;

public partial class ScanRootsTreeView : UserControl
{
    private const double HeaderBasePadding = 4;
    private ScanRootsTreeViewModel? _vm;
    private INotifyCollectionChanged? _rowsNotify;
    private IScanRootsTreeViewContext? _viewContext;
    private ScrollViewer? _scroller;
    private Border? _headerHost;
    private Border? _rowsHost;

    public ScanRootsTreeView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => HookVm();
        AttachedToVisualTree += (_, _) =>
        {
            HookVm();
            LayoutUpdated += OnLayoutUpdated;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            LayoutUpdated -= OnLayoutUpdated;
            UnhookVm();
        };
    }

    private void HookVm()
    {
        UnhookVm();

        _viewContext = DataContext as IScanRootsTreeViewContext;
        _rowsNotify = _viewContext?.Rows;
        if (_rowsNotify is not null)
            _rowsNotify.CollectionChanged += RowsOnCollectionChanged;

        _headerHost = this.FindControl<Border>("PART_HeaderHost");
        _rowsHost = this.FindControl<Border>("PART_RowsHost");
        _scroller = this.FindControl<ScrollViewer>("PART_Scroller");
        if (_scroller is not null)
            _scroller.ScrollChanged += ScrollerOnScrollChanged;

        _vm = DataContext as ScanRootsTreeViewModel;
        if (_vm is not null)
            _vm.RequestCenterSelectedRow += VmOnRequestCenterSelectedRow;

        UpdateEmptyStateVisibility();
        UpdateHeaderScrollbarGutter();
    }

    private void UnhookVm()
    {
        if (_rowsNotify is not null)
            _rowsNotify.CollectionChanged -= RowsOnCollectionChanged;
        _rowsNotify = null;
        _viewContext = null;

        if (_scroller is not null)
            _scroller.ScrollChanged -= ScrollerOnScrollChanged;
        _scroller = null;
        _headerHost = null;
        _rowsHost = null;

        if (_vm is not null)
            _vm.RequestCenterSelectedRow -= VmOnRequestCenterSelectedRow;

        _vm = null;
    }

    private void RowsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateChromeState();

    private void ScrollerOnScrollChanged(object? sender, ScrollChangedEventArgs e) =>
        UpdateHeaderScrollbarGutter();

    private void OnLayoutUpdated(object? sender, EventArgs e) =>
        UpdateHeaderScrollbarGutter();

    private void UpdateChromeState()
    {
        UpdateEmptyStateVisibility();
        UpdateHeaderScrollbarGutter();
    }

    private void UpdateEmptyStateVisibility()
    {
        var scroller = this.FindControl<ScrollViewer>("PART_Scroller");
        var empty = this.FindControl<Border>("PART_EmptyState");
        if (scroller is null || empty is null)
            return;

        var hasRows = _viewContext?.Rows.Count > 0;
        scroller.IsVisible = hasRows;
        empty.IsVisible = !hasRows;
    }

    private void UpdateHeaderScrollbarGutter()
    {
        if (_headerHost is null || _rowsHost is null || _scroller is null)
            return;

        var hasVerticalScrollbar = _scroller.Extent.Height - _scroller.Viewport.Height > 0.5;
        var gutter = hasVerticalScrollbar ? GetScrollbarGutter() : 0;
        var rightPadding = HeaderBasePadding + gutter;
        var targetPadding = new Thickness(HeaderBasePadding, HeaderBasePadding, rightPadding, HeaderBasePadding);
        var rowsPadding = new Thickness(0, 0, gutter, 0);

        if (_headerHost.Padding != targetPadding)
            _headerHost.Padding = targetPadding;

        if (_rowsHost.Padding != rowsPadding)
            _rowsHost.Padding = rowsPadding;
    }

    private double GetScrollbarGutter()
    {
        if (Resources.TryGetValue("ScanRootsScrollbarGutter", out var value))
        {
            if (value is double d)
                return d;
            if (value is int i)
                return i;
            if (value is float f)
                return f;
        }

        return 14;
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
