using Avalonia.Controls;

namespace DuplicateFileFinder.Gui.Controls.TreeMap;

public interface ITreeMapNodeElement
{
    double Value { get; }               // layout metric
    public Control CreateToolTip();     // rich tooltip content
    string Label { get; }               // useful for debugging / optional labels
}