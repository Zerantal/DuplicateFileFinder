using Avalonia.Media;

namespace DuplicateFileFinder.Gui.Controls.TreeMap;

public sealed class TreeMapNode<T> where T : ITreeMapNodeElement
{
    public required T Element { get; init; }
    public required IReadOnlyList<TreeMapNode<T>> Children { get; init; } = [];

    public double Value => Element.Value;

    public IBrush? Fill { get; set; }

    public string Label => Element.Label;

    public bool HasChildren => Children.Count > 0;
}