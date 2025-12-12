using Avalonia.Media;

namespace DuplicateFileFinder.Gui.Controls;

public sealed class TreeMapNode
{
    /// <summary>Display label (e.g. directory name, group name).</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    ///     Metric used for area: bytes, etc.
    ///     For directories, this can be left at 0; the control will sum children.
    /// </summary>
    public double Value { get; set; }

    public IBrush? Fill { get; set; }

    /// <summary>Children in the hierarchy.</summary>
    public IReadOnlyList<TreeMapNode> Children { get; init; } = [];

    /// <summary>True if this node represents a directory.</summary>
    public bool IsDirectory { get; init; }

    public bool HasChildren => Children.Count > 0;
}