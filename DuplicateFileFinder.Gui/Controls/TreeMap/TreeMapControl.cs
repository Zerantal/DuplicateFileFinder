using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

using DuplicateFileFinderLib.Logging;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ForCanBeConvertedToForeach

namespace DuplicateFileFinder.Gui.Controls.TreeMap;

public sealed class TreeMapControl : Control
{
    private readonly Dictionary<uint, SolidColorBrush> _brushCache = new();
    private readonly Dictionary<TreeMapNode<ITreeMapNodeElement>, double> _valueCache = new();
    private readonly Dictionary<TreeMapNode<ITreeMapNodeElement>, Rect> _rectByNode = new();

    // cached property values
    private int _shadeLevelsCached = 16;
    private int _maxRectanglesCached = 25_000;
    private bool _valuesArePreSummedCached;
    private int _primaryBorderDepthCached = 3;
    private double _minBorderSizeCached = 6.0;

    private volatile uint _primaryBorderArgb;
    private volatile float _primaryBorderThickness;
    private volatile bool _primaryBorderEnabled;

    private volatile uint _secondaryBorderArgb;
    private volatile float _secondaryBorderThickness;
    private volatile bool _secondaryBorderEnabled;

    private RenderTargetBitmap? _cacheBitmap;
    private bool _cacheDirty = true;
    private PixelSize _cachePixelSize;
    private double _cacheScaling;

    // Scratch buffers to avoid per-call allocations.
    private readonly List<TreeItem> _itemsScratch = new(256);
    private readonly List<TreeItem> _rowScratch = new(64);

    // ----------------- Styled properties -----------------

    public static readonly StyledProperty<TreeMapNode<ITreeMapNodeElement>?> RootProperty =
        AvaloniaProperty.Register<TreeMapControl, TreeMapNode<ITreeMapNodeElement>?>(nameof(Root));

    public static readonly StyledProperty<int> ShadeLevelsProperty =
        AvaloniaProperty.Register<TreeMapControl, int>(nameof(ShadeLevels), 16);

    public static readonly StyledProperty<int> PrimaryBorderDepthProperty =
        AvaloniaProperty.Register<TreeMapControl, int>(nameof(PrimaryBorderDepth), 3);

    public static readonly StyledProperty<double> MinBorderSizeProperty =
        AvaloniaProperty.Register<TreeMapControl, double>(nameof(MinBorderSize), 6.0);

    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<TreeMapControl, bool>(nameof(ShowLabels));

    public static readonly StyledProperty<IBrush?> PrimaryBorderBrushProperty =
        AvaloniaProperty.Register<TreeMapControl, IBrush?>(nameof(PrimaryBorderBrush), Brushes.Black);

    public static readonly StyledProperty<double> PrimaryBorderThicknessProperty =
        AvaloniaProperty.Register<TreeMapControl, double>(nameof(PrimaryBorderThickness), 1.0);

    public static readonly StyledProperty<IBrush?> SecondaryBorderBrushProperty =
        AvaloniaProperty.Register<TreeMapControl, IBrush?>(nameof(SecondaryBorderBrush), Brushes.Gray);

    public static readonly StyledProperty<double> SecondaryBorderThicknessProperty =
        AvaloniaProperty.Register<TreeMapControl, double>(nameof(SecondaryBorderThickness), 0.5);

    public static readonly StyledProperty<int> MaxRectanglesProperty =
        AvaloniaProperty.Register<TreeMapControl, int>(nameof(MaxRectangles), 25_000);

    public static readonly StyledProperty<bool> ValuesArePreSummedProperty =
        AvaloniaProperty.Register<TreeMapControl, bool>(nameof(ValuesArePreSummed), defaultValue: false);

    // Selection should not dirty the bitmap cache.
    public static readonly StyledProperty<TreeMapNode<ITreeMapNodeElement>?> SelectedNodeProperty =
        AvaloniaProperty.Register<TreeMapControl, TreeMapNode<ITreeMapNodeElement>?>(nameof(SelectedNode));

    public static readonly StyledProperty<IBrush?> SelectionBorderBrushProperty =
        AvaloniaProperty.Register<TreeMapControl, IBrush?>(nameof(SelectionBorderBrush), Brushes.DeepSkyBlue);

    public static readonly StyledProperty<double> SelectionBorderThicknessProperty =
        AvaloniaProperty.Register<TreeMapControl, double>(nameof(SelectionBorderThickness), 2.0);

    private readonly List<LayoutItem> _layout = new();
    private int _rectCount;

    static TreeMapControl()
    {
        RootProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) =>
        {
            ctrl._layout.Clear();
            ctrl._rectByNode.Clear();
            ctrl._valueCache.Clear();
            ctrl._cacheBitmap?.Dispose();
            ctrl._cacheBitmap = null;
            ctrl._cacheDirty = true;

            ctrl.InvalidateMeasure();
            ctrl.InvalidateVisual();
        });

        ShadeLevelsProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshCachedProps());
        MaxRectanglesProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshCachedProps());
        ValuesArePreSummedProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshCachedProps());

        PrimaryBorderDepthProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshCachedProps());
        MinBorderSizeProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshCachedProps());

        PrimaryBorderBrushProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshBorderCache());
        SecondaryBorderBrushProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshBorderCache());
        PrimaryBorderThicknessProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshBorderCache());
        SecondaryBorderThicknessProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshBorderCache());

        // Selection must redraw the control, but must NOT dirty the bitmap cache.
        SelectedNodeProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.InvalidateVisual());
        SelectionBorderBrushProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.InvalidateVisual());
        SelectionBorderThicknessProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.InvalidateVisual());

    }

    public TreeMapControl()
    {
        RefreshCachedProps();
        RefreshBorderCache();
    }

    // -------- CLR wrappers --------

    public TreeMapNode<ITreeMapNodeElement>? Root
    {
        get => GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public int ShadeLevels
    {
        get => GetValue(ShadeLevelsProperty);
        set => SetValue(ShadeLevelsProperty, value);
    }

    public int PrimaryBorderDepth
    {
        get => GetValue(PrimaryBorderDepthProperty);
        set => SetValue(PrimaryBorderDepthProperty, value);
    }

    public double MinBorderSize
    {
        get => GetValue(MinBorderSizeProperty);
        set => SetValue(MinBorderSizeProperty, value);
    }

    public bool ShowLabels
    {
        get => GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    public IBrush? PrimaryBorderBrush
    {
        get => GetValue(PrimaryBorderBrushProperty);
        set => SetValue(PrimaryBorderBrushProperty, value);
    }

    public double PrimaryBorderThickness
    {
        get => GetValue(PrimaryBorderThicknessProperty);
        set => SetValue(PrimaryBorderThicknessProperty, value);
    }

    public IBrush? SecondaryBorderBrush
    {
        get => GetValue(SecondaryBorderBrushProperty);
        set => SetValue(SecondaryBorderBrushProperty, value);
    }

    public double SecondaryBorderThickness
    {
        get => GetValue(SecondaryBorderThicknessProperty);
        set => SetValue(SecondaryBorderThicknessProperty, value);
    }

    public int MaxRectangles
    {
        get => GetValue(MaxRectanglesProperty);
        set => SetValue(MaxRectanglesProperty, value);
    }

    public bool ValuesArePreSummed
    {
        get => GetValue(ValuesArePreSummedProperty);
        set => SetValue(ValuesArePreSummedProperty, value);
    }

    public TreeMapNode<ITreeMapNodeElement>? SelectedNode
    {
        get => GetValue(SelectedNodeProperty);
        set => SetValue(SelectedNodeProperty, value);
    }

    public IBrush? SelectionBorderBrush
    {
        get => GetValue(SelectionBorderBrushProperty);
        set => SetValue(SelectionBorderBrushProperty, value);
    }

    public double SelectionBorderThickness
    {
        get => GetValue(SelectionBorderThicknessProperty);
        set => SetValue(SelectionBorderThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height;
        return new Size(w, h);
    }

    private void RefreshCachedProps()
    {
        _shadeLevelsCached = Math.Max(1, ShadeLevels);
        _maxRectanglesCached = MaxRectangles;
        _valuesArePreSummedCached = ValuesArePreSummed;

        _primaryBorderDepthCached = PrimaryBorderDepth;
        _minBorderSizeCached = MinBorderSize;

        _cacheDirty = true;
    }

    private void RefreshBorderCache()
    {
        (_primaryBorderEnabled, _primaryBorderArgb) = TryBrushToArgb(PrimaryBorderBrush);
        _primaryBorderThickness = (float)PrimaryBorderThickness;
        if (_primaryBorderThickness <= 0)
            _primaryBorderEnabled = false;

        (_secondaryBorderEnabled, _secondaryBorderArgb) = TryBrushToArgb(SecondaryBorderBrush);
        _secondaryBorderThickness = (float)SecondaryBorderThickness;
        if (_secondaryBorderThickness <= 0)
            _secondaryBorderEnabled = false;

        _cacheDirty = true;
        InvalidateVisual();
    }

    private static (bool ok, uint argb) TryBrushToArgb(IBrush? brush)
    {
        if (brush is SolidColorBrush sb)
        {
            var c = sb.Color;
            uint argb = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            return (true, argb);
        }

        if (brush is ImmutableSolidColorBrush ib)
        {
            var c = ib.Color;
            uint argb = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            return (true, argb);
        }

        return (false, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        using (TimingLog.Start("TreeMapControl.ArrangeOverride"))
        {
            RefreshCachedProps();

            _layout.Clear();
            _layout.EnsureCapacity(_maxRectanglesCached + _shadeLevelsCached);

            _rectByNode.Clear();
            _valueCache.Clear();

            _rectCount = 0;

            if (Root == null || finalSize.Width <= 0 || finalSize.Height <= 0)
                return finalSize;

            var total = GetNodeValue(Root);
            if (total <= 0)
                return finalSize;

            var bounds = new Rect(0, 0, finalSize.Width, finalSize.Height);

            // Emit root
            EmitRect(Root, bounds, 0, null, 0, out var rootBaseColor, out var rootBaseDepth);

            // Best-first expansion: expand biggest rectangles first.
            var pq = new PriorityQueue<ExpandFrame, double>();

            // Phase A: expand root's children once so all top-level nodes appear.
            LayoutChildrenFrames(Root, bounds, 0, rootBaseColor, rootBaseDepth, pq);

            // Phase B: expand the largest directory rects until cap.
            while (_rectCount < _maxRectanglesCached && pq.TryDequeue(out var frame, out _))
            {
                if (!frame.Node.HasChildren)
                    continue;

                LayoutChildrenFrames(frame.Node, frame.Bounds, frame.Depth, frame.BaseColor, frame.BaseDepth, pq);
            }

            RebuildCacheBitmapIfNeeded(finalSize);
        }

        return finalSize;
    }

    // ----------------- Layout helpers -----------------

    private void EmitRect(
        TreeMapNode<ITreeMapNodeElement> node,
        Rect bounds,
        int depth,
        Color? inheritedBaseColor,
        int baseDepth,
        out Color? outBaseColor,
        out int outBaseDepth)
    {
        // Determine colour origin for this subtree.
        if (node.Fill is SolidColorBrush solid)
        {
            outBaseColor = solid.Color;
            outBaseDepth = depth;
        }
        else
        {
            outBaseColor = inheritedBaseColor;
            outBaseDepth = baseDepth;
        }

        var fill = GetEffectiveBrush(depth, outBaseColor, outBaseDepth);
        var fillColor = fill switch
        {
            SolidColorBrush sb => sb.Color,
            ImmutableSolidColorBrush ib => ib.Color,
            _ => Colors.Gray
        };

        _layout.Add(new LayoutItem
        {
            Rect = bounds,
            Node = node,
            Depth = depth,
            Fill = fill,
            FillColor = fillColor
        });

        _rectByNode[node] = bounds;
        _rectCount++;
    }

    private void LayoutChildrenFrames(
        TreeMapNode<ITreeMapNodeElement> node,
        Rect bounds,
        int depth,
        Color? inheritedBaseColor,
        int baseDepth,
        PriorityQueue<ExpandFrame, double> pq)
    {
        if (_rectCount >= _maxRectanglesCached)
            return;

        if (!node.HasChildren)
            return;

        // Border margin logic (same semantics as before).
        var minSize = _minBorderSizeCached;
        var canDrawBorder = bounds.Width >= minSize && bounds.Height >= minSize;
        var usePrimary = depth <= _primaryBorderDepthCached;
        var thickness = usePrimary ? _primaryBorderThickness : _secondaryBorderThickness;
        var margin = (canDrawBorder && thickness > 0) ? thickness : 0.0;

        var inner = bounds.Deflate(new Thickness(margin));
        if (inner.Width <= 0 || inner.Height <= 0)
            return;

        // Build treemap items (single pass, reuse scratch list).
        _itemsScratch.Clear();

        var children = node.Children;
        if (_itemsScratch.Capacity < children.Count)
            _itemsScratch.Capacity = children.Count;

        double total = 0;
        foreach (var c in children)
        {
            var v = Math.Max(0, GetNodeValue(c));
            if (v <= 0)
                continue;

            _itemsScratch.Add(new TreeItem { Node = c, Value = v });
            total += v;
        }

        if (_itemsScratch.Count == 0 || total <= 0)
            return;

        // Scale values into areas for this rect.
        var totalArea = inner.Width * inner.Height;
        var scale = totalArea / total;

        for (int i = 0; i < _itemsScratch.Count; i++)
        {
            var ti = _itemsScratch[i];
            ti.Area = ti.Value * scale;
            _itemsScratch[i] = ti;
        }

        // Consume produced rects immediately: emit + enqueue frames.
        var consumer = new ChildRectConsumer(
            owner: this,
            pq: pq,
            childDepth: depth + 1,
            inheritedBaseColor: inheritedBaseColor,
            baseDepth: baseDepth);

        SquarifyFlat(_itemsScratch, inner, ref consumer);
    }

    private void SquarifyFlat<T>(List<TreeItem> items, Rect rect, ref T consumer)
        where T : struct, IRectConsumer
    {
        if (items.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
            return;

        _rowScratch.Clear();
        if (_rowScratch.Capacity < Math.Min(items.Count, 64))
            _rowScratch.Capacity = Math.Min(items.Count, 64);

        int index = 0;

        while (index < items.Count && rect is { Width: > 0, Height: > 0 })
        {
            _rowScratch.Clear();

            bool horizontal = rect.Width >= rect.Height;
            double w = horizontal ? rect.Width : rect.Height;

            // Start row with first item
            var first = items[index++];
            _rowScratch.Add(first);

            double rowArea = first.Area;
            double minA = first.Area;
            double maxA = first.Area;

            double bestWorst = WorstAspectFromStats(rowArea, minA, maxA, w);

            // Greedily grow row while aspect ratio improves
            while (index < items.Count)
            {
                var candidate = items[index];

                double newRowArea = rowArea + candidate.Area;
                double newMinA = candidate.Area < minA ? candidate.Area : minA;
                double newMaxA = candidate.Area > maxA ? candidate.Area : maxA;

                double newWorst = WorstAspectFromStats(newRowArea, newMinA, newMaxA, w);

                if (newWorst <= bestWorst)
                {
                    _rowScratch.Add(candidate);
                    rowArea = newRowArea;
                    minA = newMinA;
                    maxA = newMaxA;
                    bestWorst = newWorst;
                    index++;
                }
                else
                {
                    break;
                }
            }

            if (rowArea <= 0)
                return;

            if (horizontal)
            {
                double rowHeight = rowArea / rect.Width;
                double x = rect.X;

                foreach (var item in _rowScratch)
                {
                    double itemWidth = item.Area / rowHeight;
                    consumer.Consume(item.Node, new Rect(x, rect.Y, itemWidth, rowHeight));
                    x += itemWidth;
                }

                rect = new Rect(rect.X, rect.Y + rowHeight, rect.Width, Math.Max(0, rect.Height - rowHeight));
            }
            else
            {
                double rowWidth = rowArea / rect.Height;
                double y = rect.Y;

                foreach (var item in _rowScratch)
                {
                    double itemHeight = item.Area / rowWidth;
                    consumer.Consume(item.Node, new Rect(rect.X, y, rowWidth, itemHeight));
                    y += itemHeight;
                }

                rect = new Rect(rect.X + rowWidth, rect.Y, Math.Max(0, rect.Width - rowWidth), rect.Height);
            }
        }
    }

    private static double WorstAspectFromStats(double sumArea, double minArea, double maxArea, double w)
    {
        if (sumArea <= 0 || minArea <= 0 || w <= 0)
            return double.MaxValue;

        double s2 = sumArea * sumArea;
        double w2 = w * w;

        // Same formula as WorstAspect(row,w), but computed from (sum, min, max) in O(1)
        double a = (w2 * maxArea) / s2;
        double b = s2 / (w2 * minArea);
        return a > b ? a : b;
    }

    // ----------------- Click / hit test -----------------

    TreeMapNode<ITreeMapNodeElement>? HitTestNode(Point position)
    {
        TreeMapNode<ITreeMapNodeElement>? hit = null;
        for (var i = _layout.Count - 1; i >= 0; i--)
        {
            var item = _layout[i];
            if (item.Rect.Contains(position))
            {
                hit = item.Node;
                break;
            }
        }

        return hit;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        using (TimingLog.Start("TreeMapControl.OnPointerPressed"))
        {
            base.OnPointerPressed(e);

            if (_layout.Count == 0)
                return;

            var p = e.GetPosition(this);
            var hit = HitTestNode(p);
            if (hit is null)
                return;

            var props = e.GetCurrentPoint(this).Properties;
            var isRight = props.IsRightButtonPressed;
            var isLeft = props.IsLeftButtonPressed;

            // Only react to primary buttons
            if (!isLeft && !isRight)
                return;

            // Select the hit node before any context menu opens.
            if (!ReferenceEquals(SelectedNode, hit))
                SelectedNode = hit;

            // If right-click: do NOT mark handled, so context menu can open normally.
            if (isLeft)
                e.Handled = true;

            // remove tooltip and re-arm it.
            _currentNodeUnderPointer = null;
            ToolTip.SetTip(this, null);
        }
    }

    // ----------------- Rendering -----------------

    public override void Render(DrawingContext context)
    {
        using (TimingLog.Start("TreeMapControl.Render"))
        {
            base.Render(context);

            var bmp = _cacheBitmap;
            if (bmp is not null)
            {
                context.DrawImage(
                    bmp,
                    new Rect(0, 0, bmp.Size.Width, bmp.Size.Height),
                    new Rect(0, 0, Bounds.Width, Bounds.Height));
            }

            DrawSelectionOverlay(context);
        }
    }

    private void DrawSelectionOverlay(DrawingContext context)
    {
        var selected = SelectedNode;
        if (selected is null)
            return;

        if (!_rectByNode.TryGetValue(selected, out var rect))
            return;

        var t = SelectionBorderThickness;
        if (t <= 0)
            return;

        var brush = SelectionBorderBrush;
        if (brush is null)
            return;

        var pen = new Pen(brush, t);
        var r = rect.Deflate(new Thickness(t / 2));

        if (r is { Width: > 0, Height: > 0 })
            context.DrawRectangle(pen, r);
    }

    private void RebuildCacheBitmapIfNeeded(Size arrangedSize)
    {
        var toplevel = TopLevel.GetTopLevel(this);
        var scaling = toplevel?.RenderScaling ?? 1.0;

        // Pixel size (rounded) for current control size
        var pxW = Math.Max(1, (int)Math.Ceiling(arrangedSize.Width * scaling));
        var pxH = Math.Max(1, (int)Math.Ceiling(arrangedSize.Height * scaling));
        var pixelSize = new PixelSize(pxW, pxH);

        var needNew =
            _cacheBitmap is null ||
            _cacheDirty ||
            Math.Abs(_cacheScaling - scaling) > 0.0001 ||
            _cachePixelSize != pixelSize;

        if (!needNew)
            return;

        _cacheBitmap?.Dispose();
        _cacheBitmap = new RenderTargetBitmap(pixelSize);
        _cachePixelSize = pixelSize;
        _cacheScaling = scaling;

        // Draw the treemap ONCE into the bitmap
        using (var dc = _cacheBitmap.CreateDrawingContext(clear: true))
        {
            // Map pixels->DIPs
            using (dc.PushTransform(Matrix.CreateScale(1.0 / scaling, 1.0 / scaling)))
            {
                DrawTreemapInto(dc);
            }
        }

        _cacheDirty = false;
        InvalidateVisual();
    }

    private void DrawTreemapInto(DrawingContext dc)
    {
        // Fills
        for (var i = 0; i < _layout.Count; i++)
        {
            var li = _layout[i];
            dc.FillRectangle(li.Fill, li.Rect);
        }

        // Borders
        var minSize = _minBorderSizeCached;
        var primaryDepth = _primaryBorderDepthCached;

        IPen? primaryPen = null;
        IPen? secondaryPen = null;

        if (_primaryBorderEnabled && _primaryBorderThickness > 0)
            primaryPen = new Pen(new SolidColorBrush(ColorFromArgb(_primaryBorderArgb)), _primaryBorderThickness);

        if (_secondaryBorderEnabled && _secondaryBorderThickness > 0)
            secondaryPen = new Pen(new SolidColorBrush(ColorFromArgb(_secondaryBorderArgb)), _secondaryBorderThickness);

        for (var i = 0; i < _layout.Count; i++)
        {
            var li = _layout[i];
            var r = li.Rect;

            if (r.Width < minSize || r.Height < minSize)
                continue;

            var pen = (li.Depth <= primaryDepth) ? primaryPen : secondaryPen;
            if (pen is not null)
                dc.DrawRectangle(pen, r);
        }

        // Labels (leaf-only, centered, contrast-aware)
        if (!ShowLabels)
            return;

        for (var i = 0; i < _layout.Count; i++)
        {
            var li = _layout[i];

            if (!ShouldLabelNode(li.Node))
                continue;

            // Choose a label text. Prefer something short:
            var label = li.Node.Element.Label;
            if (string.IsNullOrWhiteSpace(label))
                continue;

            DrawCenteredLabel(dc, label, li.Rect, li.FillColor);
        }
    }


    private static Color ColorFromArgb(uint argb)
    {
        var a = (byte)((argb >> 24) & 0xFF);
        var r = (byte)((argb >> 16) & 0xFF);
        var g = (byte)((argb >> 8) & 0xFF);
        var b = (byte)(argb & 0xFF);
        return Color.FromArgb(a, r, g, b);
    }

    // ----------------- Labels -----------------

    private const double LabelFontSize = 11;
    private const double LabelPadding = 2;
    private const double MinLabelWidth = 60;
    private const double MinLabelHeight = 18;

    // Cache black/white brushes to avoid churn
    private static readonly IBrush s_labelBrushLight = Brushes.White;
    private static readonly IBrush s_labelBrushDark = Brushes.Black;

    private static bool ShouldLabelNode(TreeMapNode<ITreeMapNodeElement> node)
    {
        // Strict: only leaves
        return !node.HasChildren;
    }

    private static IBrush GetContrastBrush(Color background)
    {
        // Relative luminance (sRGB)
        static double Linearize(byte c)
        {
            var s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        var r = Linearize(background.R);
        var g = Linearize(background.G);
        var b = Linearize(background.B);

        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;

        // Threshold tuned for “looks right” on mid greys
        return luminance < 0.45 ? s_labelBrushLight : s_labelBrushDark;
    }

    private void DrawCenteredLabel(DrawingContext ctx, string text, Rect rect, Color bg)
    {
        // Fast reject before allocating FormattedText
        if (rect.Width < MinLabelWidth || rect.Height < MinLabelHeight)
            return;

        var brush = GetContrastBrush(bg);

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            LabelFontSize,
            brush);

        // Fit check with padding
        if (formatted.Width + 2 * LabelPadding > rect.Width ||
            formatted.Height + 2 * LabelPadding > rect.Height)
            return;

        // Center in rect
        var x = rect.X + (rect.Width - formatted.Width) / 2.0;
        var y = rect.Y + (rect.Height - formatted.Height) / 2.0;

        // Clamp a bit to avoid drawing exactly on edges
        x = Math.Max(rect.X + LabelPadding, x);
        y = Math.Max(rect.Y + LabelPadding, y);

        ctx.DrawText(formatted, new Point(x, y));
    }


    // ----------------- Value aggregation -----------------

    // recursively aggregate value and store in _valueCache IF ValuesArePreSummed = false
    private double GetNodeValue(TreeMapNode<ITreeMapNodeElement> node)
    {
        if (_valuesArePreSummedCached)
            return Math.Max(0, node.Element.Value);

        if (_valueCache.TryGetValue(node, out var cached))
            return cached;

        double sum;
        if (!node.HasChildren)
        {
            sum = Math.Max(0, node.Element.Value);
        }
        else
        {
            sum = 0;
            foreach (var child in node.Children)
                sum += GetNodeValue(child);
        }

        _valueCache[node] = sum;
        return sum;
    }

    // ----------------- Colour handling -----------------

    private Color ShadeColor(Color baseColor, int depthFromBase)
    {
        if (depthFromBase <= 0)
            return baseColor;

        var levels = _shadeLevelsCached;

        // If beyond levels, go to black.
        if (depthFromBase >= levels)
            return Color.FromArgb(baseColor.A, 0, 0, 0);

        // Integer fade: factor = (levels - depth)/levels
        var numer = levels - depthFromBase;

        byte r = (byte)(baseColor.R * numer / levels);
        byte g = (byte)(baseColor.G * numer / levels);
        byte b = (byte)(baseColor.B * numer / levels);

        return Color.FromArgb(baseColor.A, r, g, b);
    }

    private SolidColorBrush GetCachedBrush(Color c)
    {
        uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        if (_brushCache.TryGetValue(key, out var b))
            return b;

        b = new SolidColorBrush(c);
        _brushCache[key] = b;
        return b;
    }

    private IBrush GetEffectiveBrush(int depth, Color? baseColor, int baseDepth)
    {
        if (baseColor.HasValue)
        {
            var color = ShadeColor(baseColor.Value, depth - baseDepth);
            return GetCachedBrush(color);
        }

        // Fallback palette if no base colour origin exists.
        return depth switch
        {
            0 => GetCachedBrush(Color.Parse("#FFE0E0E0")),
            1 => GetCachedBrush(Color.Parse("#FFC0C0C0")),
            2 => GetCachedBrush(Color.Parse("#FFA0A0A0")),
            _ => GetCachedBrush(Color.Parse("#FF808080"))
        };
    }

    // ----------------- Tooltips -----------------

    private ITreeMapNodeElement? _currentNodeUnderPointer;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_layout.Count == 0)
            return;

        var p = e.GetPosition(this);

        var hit = HitTestNode(p);

        if (hit is null)
        {
            if (_currentNodeUnderPointer is not null)
            {
                _currentNodeUnderPointer = null;
                ToolTip.SetTip(this, null);
            }
            return;
        }

        if (!ReferenceEquals(_currentNodeUnderPointer, hit.Element))
        {
            _currentNodeUnderPointer = hit.Element;

            var tip = hit.Element.ToolTipFactory();
            ToolTip.SetTip(this, tip);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        if (_currentNodeUnderPointer is not null)
        {
            _currentNodeUnderPointer = null;
            ToolTip.SetTip(this, null);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _cacheBitmap?.Dispose();
        _cacheBitmap = null;
    }

    protected override void OnMeasureInvalidated()
    {
        base.OnMeasureInvalidated();
        _cacheDirty = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _cacheDirty = true;
    }

    private sealed class ExpandFrame
    {
        public required TreeMapNode<ITreeMapNodeElement> Node { get; init; }
        public required Rect Bounds { get; init; }
        public required int Depth { get; init; }
        public required Color? BaseColor { get; init; }
        public required int BaseDepth { get; init; }
    }

    // ----------------- Internal layout model -----------------

    private sealed class LayoutItem
    {
        public required Rect Rect { get; init; }
        public required TreeMapNode<ITreeMapNodeElement> Node { get; init; }
        public required int Depth { get; init; }
        public required IBrush Fill { get; init; }
        public required Color FillColor { get; init; }
    }

    // ----------------- Squarified treemap -----------------

    private sealed class TreeItem
    {
        public required TreeMapNode<ITreeMapNodeElement> Node { get; init; }
        public required double Value { get; init; }
        public double Area { get; set; }
    }

    private interface IRectConsumer
    {
        void Consume(TreeMapNode<ITreeMapNodeElement> node, Rect rect);
    }

    private readonly struct ChildRectConsumer(
        TreeMapControl owner,
        PriorityQueue<ExpandFrame, double> pq,
        int childDepth,
        Color? inheritedBaseColor,
        int baseDepth)
        : IRectConsumer
    {
        public void Consume(TreeMapNode<ITreeMapNodeElement> node, Rect rect)
        {
            if (owner._rectCount >= owner._maxRectanglesCached)
                return;

            owner.EmitRect(node, rect, childDepth, inheritedBaseColor, baseDepth,
                out var childBaseColor, out var childBaseDepth);

            if (node.HasChildren)
            {
                var area = rect.Width * rect.Height;
                pq.Enqueue(new ExpandFrame
                {
                    Node = node,
                    Bounds = rect,
                    Depth = childDepth,
                    BaseColor = childBaseColor,
                    BaseDepth = childBaseDepth
                }, -area);
            }
        }
    }
}
