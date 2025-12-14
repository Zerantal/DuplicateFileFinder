using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

// Used to hold ScanRoots or as a summary node when total node count becomes excessive
public sealed class SyntheticTreeMapElement : RepoTreeMapElement
{
    [SetsRequiredMembers]
    public SyntheticTreeMapElement(
        string label,
        double value,
        string typeLabel,
        IReadOnlyList<(string Key, string Value)> lines)
    {
        ScanRoot = null;

        Name = label;
        Label = label;
        Value = value;

        TypeLabel = typeLabel;
        RelativePathFactory = () => string.Empty;

        // Convert tuples -> KeyValuePair for easy XAML binding
        var list = new List<KeyValuePair<string, string>>(lines.Count);
        foreach (var (k, v) in lines)
            list.Add(new KeyValuePair<string, string>(k, v));
        ToolTipLines = list;
    }

    public string TypeLabel { get; }
    public IReadOnlyList<KeyValuePair<string, string>> ToolTipLines { get; }
}