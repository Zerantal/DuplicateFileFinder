using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

// ReSharper disable MemberCanBePrivate.Global

namespace DuplicateFileFinder.Gui.Controls.TreeMap;

public sealed class TreeMapControl : Control
{
    private readonly Dictionary<TreeMapNode<ITreeMapNodeElement>, double> _valueCache = new();
    private Control? _currentTooltip;
    
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

    protected override Size ArrangeOverride(Size finalSize)
    {
        _layout.Clear();
        _valueCache.Clear();
        _rectCount = 0;

        if (Root == null || finalSize.Width <= 0 || finalSize.Height <= 0)
            return finalSize;

        var total = GetNodeValue(Root);
        if (total <= 0)
            return finalSize;

        var bounds = new Rect(0, 0, finalSize.Width, finalSize.Height);

        // Lay out the dummy root rect itself (optional but harmless).
        EmitRect(Root, bounds, 0, null, 0, out var rootBaseColor, out var rootBaseDepth);

        // Best-first expansion: expand biggest rectangles first.
        var pq = new PriorityQueue<ExpandFrame, double>();

        // Phase A: layout children of root once so ALL nodes immediately under root appear.
        foreach (var childFrame in LayoutChildrenFrames(Root, bounds, 0, rootBaseColor, rootBaseDepth))
            // PriorityQueue in .NET is min-heap; use negative area for max-heap behavior.
            pq.Enqueue(childFrame, -childFrame.Bounds.Width * childFrame.Bounds.Height);

        // Phase B: expand the largest directory rects first until cap.
        while (_rectCount < MaxRectangles && pq.TryDequeue(out var frame, out _))
        {
            if (_rectCount >= MaxRectangles)
                break;

            if (!frame.Node.HasChildren)
                continue;

            foreach (var next in LayoutChildrenFrames(frame.Node, frame.Bounds, frame.Depth, frame.BaseColor,
                         frame.BaseDepth)) pq.Enqueue(next, -next.Bounds.Width * next.Bounds.Height);
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

    private IEnumerable<ExpandFrame> LayoutChildrenFrames(
        TreeMapNode<ITreeMapNodeElement> node,
        Rect bounds,
        int depth,
        Color? inheritedBaseColor,
        int baseDepth)
    {
        if (_rectCount >= MaxRectangles)
            yield break;

        if (!node.HasChildren)
            yield break;

        // Only reserve margin if we expect to draw a border on this node.
        var minSize = MinBorderSize;
        var canDrawBorder = bounds.Width >= minSize && bounds.Height >= minSize;
        var usePrimary = depth <= PrimaryBorderDepth;
        var thickness = usePrimary ? PrimaryBorderThickness : SecondaryBorderThickness;
        var margin = canDrawBorder && thickness > 0 ? thickness : 0.0;

        var inner = bounds.Deflate(new Thickness(margin));
        if (inner.Width <= 0 || inner.Height <= 0)
            yield break;

        // Build treemap items (children must have Value set; dirs are aggregated by GetNodeValue).
        var items = node.Children
            .Select(c => new TreeItem { Node = c, Value = Math.Max(0, c.Value) })
            .Where(i => i.Value > 0)
            .ToList();

        if (items.Count == 0)
            yield break;

        var total = items.Sum(i => i.Value);
        if (total <= 0)
            yield break;

        var totalArea = inner.Width * inner.Height;
        var scale = totalArea / total;
        foreach (var item in items)
            item.Area = item.Value * scale;

        // Squarify items into rectangles within 'inner' WITHOUT recursing into grandchildren.
        // For each produced child rectangle: emit rect + yield frame if directory.
        foreach (var (child, rect) in SquarifyFlat(items, inner))
        {
            if (_rectCount >= MaxRectangles)
                yield break;

            EmitRect(child, rect, depth + 1, inheritedBaseColor, baseDepth, out var childBaseColor,
                out var childBaseDepth);

            if (child.HasChildren)
                yield return new ExpandFrame
                {
                    Node = child,
                    Bounds = rect,
                    Depth = depth + 1,
                    BaseColor = childBaseColor,
                    BaseDepth = childBaseDepth
                };
        }
    }

    private IEnumerable<(TreeMapNode<ITreeMapNodeElement> Node, Rect Rect)> SquarifyFlat(List<TreeItem> items, Rect rect)
    {
        var index = 0;

        while (index < items.Count && rect is { Width: > 0, Height: > 0 })
        {
            var row = new List<TreeItem> { items[index] };
            index++;

            var horizontal = rect.Width >= rect.Height;
            var w = horizontal ? rect.Width : rect.Height;
            var bestWorst = WorstAspect(row, w);

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
                    row.RemoveAt(row.Count - 1);
                    break;
                }
            }

            var rowArea = row.Sum(i => i.Area);
            if (rowArea <= 0)
                yield break;

            if (horizontal)
            {
                var rowHeight = rowArea / rect.Width;
                var x = rect.X;

                foreach (var item in row)
                {
                    var itemWidth = item.Area / rowHeight;
                    var r = new Rect(x, rect.Y, itemWidth, rowHeight);
                    yield return (item.Node, r);
                    x += itemWidth;
                }

                rect = new Rect(rect.X, rect.Y + rowHeight, rect.Width, Math.Max(0, rect.Height - rowHeight));
            }
            else
            {
                var rowWidth = rowArea / rect.Height;
                var y = rect.Y;

                foreach (var item in row)
                {
                    var itemHeight = item.Area / rowWidth;
                    var r = new Rect(rect.X, y, rowWidth, itemHeight);
                    yield return (item.Node, r);
                    y += itemHeight;
                }

                rect = new Rect(rect.X + rowWidth, rect.Y, Math.Max(0, rect.Width - rowWidth), rect.Height);
            }
        }
    }


    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var primaryDepth = PrimaryBorderDepth;
        var minSize = MinBorderSize;
        var primaryBrush = PrimaryBorderBrush;
        var secondaryBrush = SecondaryBorderBrush;
        var primaryThickness = PrimaryBorderThickness;
        var secondaryThickness = SecondaryBorderThickness;

        foreach (var item in _layout)
        {
            var rect = item.Rect;
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;

            context.FillRectangle(item.Fill, rect);

            if (rect.Width >= minSize && rect.Height >= minSize)
            {
                var usePrimary = item.Depth <= primaryDepth;
                var brush = usePrimary ? primaryBrush : secondaryBrush;
                var thickness = usePrimary ? primaryThickness : secondaryThickness;

                if (brush != null && thickness > 0)
                {
                    var pen = new Pen(brush, thickness);
                    context.DrawRectangle(pen, rect);
                }
            }

            if (ShowLabels) DrawLabel(context, item.Node.Label, rect);
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
        if (ValuesArePreSummed)
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

        var levels = ShadeLevels <= 0 ? 1 : ShadeLevels;
        var t = Math.Clamp(depthFromBase / (double)levels, 0.0, 1.0);

        var r = (byte)(baseColor.R * (1.0 - t));
        var g = (byte)(baseColor.G * (1.0 - t));
        var b = (byte)(baseColor.B * (1.0 - t));

        return Color.FromArgb(baseColor.A, r, g, b);
    }

    private IBrush GetEffectiveBrush(int depth, Color? baseColor, int baseDepth)
    {
        if (baseColor.HasValue)
        {
            var color = ShadeColor(baseColor.Value, depth - baseDepth);
            return new SolidColorBrush(color);
        }

        // Fallback palette if no base colour origin exists.
        return depth switch
        {
            0 => new SolidColorBrush(Color.Parse("#FFE0E0E0")),
            1 => new SolidColorBrush(Color.Parse("#FFC0C0C0")),
            2 => new SolidColorBrush(Color.Parse("#FFA0A0A0")),
            _ => new SolidColorBrush(Color.Parse("#FF808080"))
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
        if (_rectCount >= MaxRectangles)
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

    private static double WorstAspect(IReadOnlyList<TreeItem> row, double w)
    {
        if (row.Count == 0 || w <= 0)
            return double.MaxValue;

        var sum = row.Sum(i => i.Area);
        if (sum <= 0)
            return double.MaxValue;

        var maxA = row.Max(i => i.Area);
        var minA = row.Min(i => i.Area);

        var s2 = sum * sum;
        var w2 = w * w;

        return Math.Max(
            w2 * maxA / s2,
            s2 / (w2 * minA));
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

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_layout.Count == 0)
            return;

        var p = e.GetPosition(this);
        Control? tip = null;

        // Iterate from back so visually “topmost” items win.
        for (var i = _layout.Count - 1; i >= 0; i--)
        {
            var item = _layout[i];
            if (item.Rect.Contains(p))
            {
                tip = item.Node.Element.CreateToolTip();
                break;
            }
        }
        
        if (!ReferenceEquals(_currentTooltip, tip))
        {
            _currentTooltip = tip;
            ToolTip.SetTip(this, tip);
        }
            
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        ToolTip.SetTip(this, null);
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
}