using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using DuplicateFileFinderLib.Logging;

// ReSharper disable MemberCanBePrivate.Global

namespace DuplicateFileFinder.Gui.Controls.TreeMap;

public sealed class TreeMapControl : Control
{
    private readonly Dictionary<uint, SolidColorBrush> _brushCache = new();
    private readonly Dictionary<TreeMapNode<ITreeMapNodeElement>, double> _valueCache = new();
    private int _shadeLevelsCached = 16;
    private int _maxRectanglesCached = 25_000;
    private bool _valuesArePreSummedCached;

    // Scratch buffers to avoid per-call allocations.
    private readonly List<TreeItem> _itemsScratch = new(256);
    private readonly List<TreeItem> _rowScratch = new(64);

    // ----------------- Styled properties -----------------

    public static readonly StyledProperty<TreeMapNode<ITreeMapNodeElement>?> RootProperty =
        AvaloniaProperty.Register<TreeMapControl, TreeMapNode<ITreeMapNodeElement>?>(nameof(Root));

    public static readonly StyledProperty<int> ShadeLevelsProperty =
        AvaloniaProperty.Register<TreeMapControl, int>(
            nameof(ShadeLevels),
            16);

    /// <summary>
    ///     Depth (0 = dummy root) at which the primary border style stops.
    ///     Nodes with depth &lt;= PrimaryBorderDepth use the primary border;
    ///     deeper nodes use the secondary border style.
    /// </summary>
    public static readonly StyledProperty<int> PrimaryBorderDepthProperty =
        AvaloniaProperty.Register<TreeMapControl, int>(
            nameof(PrimaryBorderDepth),
            3);

    /// <summary>
    ///     Do not render borders for rectangles smaller than this (in px) along
    ///     either dimension.
    /// </summary>
    public static readonly StyledProperty<double> MinBorderSizeProperty =
        AvaloniaProperty.Register<TreeMapControl, double>(
            nameof(MinBorderSize),
            6.0);

    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<TreeMapControl, bool>(
            nameof(ShowLabels));

    public static readonly StyledProperty<IBrush?> PrimaryBorderBrushProperty =
        AvaloniaProperty.Register<TreeMapControl, IBrush?>(
            nameof(PrimaryBorderBrush),
            Brushes.Black);

    public static readonly StyledProperty<double> PrimaryBorderThicknessProperty =
        AvaloniaProperty.Register<TreeMapControl, double>(
            nameof(PrimaryBorderThickness),
            1.0);

    public static readonly StyledProperty<IBrush?> SecondaryBorderBrushProperty =
        AvaloniaProperty.Register<TreeMapControl, IBrush?>(
            nameof(SecondaryBorderBrush),
            Brushes.Gray);

    public static readonly StyledProperty<double> SecondaryBorderThicknessProperty =
        AvaloniaProperty.Register<TreeMapControl, double>(
            nameof(SecondaryBorderThickness),
            0.5);

    public static readonly StyledProperty<int> MaxRectanglesProperty =
        AvaloniaProperty.Register<TreeMapControl, int>(
            nameof(MaxRectangles),
            25_000);

    public static readonly StyledProperty<bool> ValuesArePreSummedProperty =
        AvaloniaProperty.Register<TreeMapControl, bool>(
            nameof(ValuesArePreSummed),
            defaultValue: false);

    private readonly List<LayoutItem> _layout = new();
    private int _rectCount;

    static TreeMapControl()
    {
        RootProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) =>
        {
            ctrl._layout.Clear();
            ctrl.InvalidateMeasure();
            ctrl.InvalidateVisual();
        });
        ShadeLevelsProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshCachedProps());
        MaxRectanglesProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshCachedProps());
        ValuesArePreSummedProperty.Changed.AddClassHandler<TreeMapControl>((ctrl, _) => ctrl.RefreshCachedProps());

    }

    // -------- CLR wrappers --------

    public TreeMapNode<ITreeMapNodeElement>? Root
    {
        get => GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    /// <summary>
    ///     Number of depth steps from a colour origin before it shades to black.
    ///     Children share their ancestor's base colour but get darker with depth.
    /// </summary>
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

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height;
        return new Size(w, h);
    }

    public bool ValuesArePreSummed
    {
        get => GetValue(ValuesArePreSummedProperty);
        set => SetValue(ValuesArePreSummedProperty, value);
    }

    private void RefreshCachedProps()
    {
        _shadeLevelsCached = Math.Max(1, ShadeLevels);

        _maxRectanglesCached = MaxRectangles;
        _valuesArePreSummedCached = ValuesArePreSummed;
    }


    protected override Size ArrangeOverride(Size finalSize)
    {
        using (TimingLog.Start("TreeMapControl.ArrangeOverride"))
        {
            RefreshCachedProps();

            _layout.Clear();
            _layout.EnsureCapacity(_maxRectanglesCached + _shadeLevelsCached);
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
        }

        return finalSize;
    }


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

        _layout.Add(new LayoutItem
        {
            Rect = bounds,
            Node = node,
            Depth = depth,
            Fill = fill
        });

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
        var minSize = MinBorderSize;
        var canDrawBorder = bounds.Width >= minSize && bounds.Height >= minSize;
        var usePrimary = depth <= PrimaryBorderDepth;
        var thickness = usePrimary ? PrimaryBorderThickness : SecondaryBorderThickness;
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

    public override void Render(DrawingContext context)
    {
        using (TimingLog.Start("TreeMapControl.Render"))
        {
            base.Render(context);

            var primaryDepth = PrimaryBorderDepth;
            var minSize = MinBorderSize;
            var primaryBrush = PrimaryBorderBrush;
            var secondaryBrush = SecondaryBorderBrush;
            var primaryThickness = PrimaryBorderThickness;
            var secondaryThickness = SecondaryBorderThickness;

            var primaryPen = (primaryBrush != null && primaryThickness > 0)
                ? new Pen(primaryBrush, primaryThickness)
                : null;
            var secondaryPen = (secondaryBrush != null && secondaryThickness > 0)
                ? new Pen(secondaryBrush, secondaryThickness)
                : null;

            foreach (var item in _layout)
            {
                var rect = item.Rect;
                if (rect.Width <= 0 || rect.Height <= 0)
                    continue;

                context.FillRectangle(item.Fill, rect);

                if (rect.Width >= minSize && rect.Height >= minSize)
                {
                    var usePrimary = item.Depth <= primaryDepth;

                    var pen = usePrimary ? primaryPen : secondaryPen;
                    if (pen != null)
                        context.DrawRectangle(pen, rect);
                }

                if (ShowLabels)
                    DrawLabel(context, item.Node.Label, rect);
            }
        }
    }

    // ----------------- Labels -----------------

    private void DrawLabel(DrawingContext ctx, string text, Rect rect)
    {
        if (string.IsNullOrEmpty(text))
            return;

        const double minWidth = 40;
        const double minHeight = 16;

        if (rect.Width < minWidth || rect.Height < minHeight)
            return;

        var typeface = Typeface.Default;
        const double fontSize = 11;

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black);

        var origin = rect.TopLeft + new Vector(2, 2);
        ctx.DrawText(formatted, origin);
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

    /// <summary>
    ///     Layout a node and squarify its children inside it.
    /// </summary>
    private void LayoutNode(
        TreeMapNode<ITreeMapNodeElement> node,
        Rect bounds,
        int depth,
        Color? inheritedBaseColor,
        int baseDepth)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        // Hard cap on number of rectangles to avoid runaway layout on huge repos.
        if (_rectCount >= _maxRectanglesCached)
            return;

        // Determine colour origin for this subtree.
        Color? baseColor;
        int thisBaseDepth;
        if (node.Fill is SolidColorBrush solid)
        {
            baseColor = solid.Color;
            thisBaseDepth = depth;
        }
        else
        {
            baseColor = inheritedBaseColor;
            thisBaseDepth = baseDepth;
        }

        // Effective fill for this node.
        var fill = GetEffectiveBrush(depth, baseColor, thisBaseDepth);

        _layout.Add(new LayoutItem
        {
            Rect = bounds,
            Node = node,
            Depth = depth,
            Fill = fill
        });
        _rectCount++;

        if (!node.HasChildren)
            return;

        // Only reserve margin if we expect to draw a border on this node.
        var minSize = MinBorderSize;
        var canDrawBorder = bounds.Width >= minSize && bounds.Height >= minSize;
        var usePrimary = depth <= PrimaryBorderDepth;
        var thickness = usePrimary ? PrimaryBorderThickness : SecondaryBorderThickness;
        var margin = canDrawBorder && thickness > 0 ? thickness : 0.0;

        var inner = bounds.Deflate(new Thickness(margin));
        if (inner.Width <= 0 || inner.Height <= 0)
            return;

        var items = node.Children
            .Select(c => new TreeItem { Node = c, Value = Math.Max(0, GetNodeValue(c)) })
            .Where(i => i.Value > 0)
            .ToList();

        if (items.Count == 0)
            return;

        var total = items.Sum(i => i.Value);
        if (total <= 0)
            return;

        var totalArea = inner.Width * inner.Height;
        var scale = totalArea / total;

        foreach (var item in items)
            item.Area = item.Value * scale;

        Squarify(items, inner, depth + 1, baseColor, thisBaseDepth);
    }

    private void Squarify(
        List<TreeItem> items,
        Rect rect,
        int depth,
        Color? baseColor,
        int baseDepth)
    {
        if (items.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
            return;

        SquarifyInternalIterative(items, rect, depth, baseColor, baseDepth);
    }

    private void SquarifyInternalIterative(
        List<TreeItem> items,
        Rect rect,
        int depth,
        Color? baseColor,
        int baseDepth)
    {
        if (items.Count == 0 || rect.Width <= 0 || rect.Height <= 0)
            return;

        var index = 0;

        while (index < items.Count && rect is { Width: > 0, Height: > 0 })
        {
            var row = new List<TreeItem> {
                // Start row with first remaining item
                items[index] };

            index++;

            var horizontal = rect.Width >= rect.Height;
            var w = horizontal ? rect.Width : rect.Height;
            var bestWorst = WorstAspect(row, w);

            // Try to grow this row as long as aspect ratio improves
            while (index < items.Count)
            {
                row.Add(items[index]);
                var newWorst = WorstAspect(row, w);

                if (newWorst <= bestWorst)
                {
                    bestWorst = newWorst;
                    index++;
                }
                else
                {
                    // Undo last add; row is final
                    row.RemoveAt(row.Count - 1);
                    break;
                }
            }

            if (row.Count == 0)
                break;

            var rowArea = row.Sum(i => i.Area);
            if (rowArea <= 0)
                break;

            rect = LayoutRow(row, rowArea, rect, depth, baseColor, baseDepth);
        }
    }

    private static double WorstAspect(List<TreeItem> row, double w)
    {
        if (row.Count == 0 || w <= 0)
            return double.MaxValue;

        double sum = 0;
        double minA = double.PositiveInfinity;
        double maxA = 0;

        for (int i = 0; i < row.Count; i++)
        {
            var a = row[i].Area;
            sum += a;
            if (a < minA)
                minA = a;
            if (a > maxA)
                maxA = a;
        }

        if (sum <= 0 || minA <= 0)
            return double.MaxValue;

        double s2 = sum * sum;
        double w2 = w * w;

        var r1 = (w2 * maxA) / s2;
        var r2 = s2 / (w2 * minA);

        return r1 > r2 ? r1 : r2;
    }

    private Rect LayoutRow(
        IReadOnlyList<TreeItem> row,
        double rowArea,
        Rect bounds,
        int depth,
        Color? baseColor,
        int baseDepth)
    {
        if (row.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0 || rowArea <= 0)
            return bounds;

        var horizontal = bounds.Width >= bounds.Height;

        if (horizontal)
        {
            var rowHeight = rowArea / bounds.Width;
            var x = bounds.X;
            var y = bounds.Y;

            foreach (var item in row)
            {
                var itemWidth = item.Area / rowHeight;
                var rect = new Rect(x, y, itemWidth, rowHeight);

                LayoutNode(item.Node, rect, depth, baseColor, baseDepth);

                x += itemWidth;
            }

            return new Rect(bounds.X, bounds.Y + rowHeight, bounds.Width, Math.Max(0, bounds.Height - rowHeight));
        }
        else
        {
            var rowWidth = rowArea / bounds.Height;
            // var x = bounds.X;
            var y = bounds.Y;

            foreach (var item in row)
            {
                var itemHeight = item.Area / rowWidth;
                var rect = new Rect(bounds.X, y, rowWidth, itemHeight);

                LayoutNode(item.Node, rect, depth, baseColor, baseDepth);

                y += itemHeight;
            }

            return new Rect(bounds.X + rowWidth, bounds.Y, Math.Max(0, bounds.Width - rowWidth), bounds.Height);
        }
    }

    // ----------------- Tooltips -----------------

    private ITreeMapNodeElement? _currentTooltipElement;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_layout.Count == 0)
            return;

        var p = e.GetPosition(this);
        ITreeMapNodeElement? element = null;

        // Iterate from back so visually “topmost” items win.
        for (var i = _layout.Count - 1; i >= 0; i--)
        {
            var item = _layout[i];
            if (item.Rect.Contains(p))
            {
                element = item.Node.Element;
                break;
            }
        }

        if (element is null)
        {
            if (_currentTooltipElement is not null)
            {
                _currentTooltipElement = null;
                ToolTip.SetTip(this, null);
            }
            return;
        }

        if (!ReferenceEquals(_currentTooltipElement, element))
        {
            _currentTooltipElement = element;

            var tip = element.ToolTipFactory();
            ToolTip.SetTip(this, tip);
        }

    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        if (_currentTooltipElement is not null)
        {
            _currentTooltipElement = null;
            ToolTip.SetTip(this, null);
        }
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

    private readonly struct ChildRectConsumer : IRectConsumer
    {
        private readonly TreeMapControl _owner;
        private readonly PriorityQueue<ExpandFrame, double> _pq;
        private readonly int _childDepth;
        private readonly Color? _inheritedBaseColor;
        private readonly int _baseDepth;

        public ChildRectConsumer(
            TreeMapControl owner,
            PriorityQueue<ExpandFrame, double> pq,
            int childDepth,
            Color? inheritedBaseColor,
            int baseDepth)
        {
            _owner = owner;
            _pq = pq;
            _childDepth = childDepth;
            _inheritedBaseColor = inheritedBaseColor;
            _baseDepth = baseDepth;
        }

        public readonly void Consume(TreeMapNode<ITreeMapNodeElement> node, Rect rect)
        {
            if (_owner._rectCount >= _owner._maxRectanglesCached)
                return;

            _owner.EmitRect(node, rect, _childDepth, _inheritedBaseColor, _baseDepth, out var childBaseColor, out var childBaseDepth);

            if (node.HasChildren)
            {
                var area = rect.Width * rect.Height;
                _pq.Enqueue(new ExpandFrame
                {
                    Node = node,
                    Bounds = rect,
                    Depth = _childDepth,
                    BaseColor = childBaseColor,
                    BaseDepth = childBaseDepth
                }, -area);
            }
        }
    }
}
