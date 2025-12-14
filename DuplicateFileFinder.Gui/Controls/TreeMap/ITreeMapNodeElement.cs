using Avalonia.Controls;

namespace DuplicateFileFinder.Gui.Controls.TreeMap;

public interface ITreeMapNodeElement
{
    double Value { get; }               // layout metric
    string Label { get; }               // useful for debugging / optional labels
    Func<Control> ToolTipFactory { get; }
}