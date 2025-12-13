using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Media;

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
        RelativePath = string.Empty;
        ToolTipLines = lines;
    }

    private string TypeLabel { get; }
    private IReadOnlyList<(string key, string value)> ToolTipLines { get; }

    protected override Func<Control> BuildToolTipFactory()
    {
        var name = Name;
        var typeLabel = TypeLabel;
        
        return () =>
        {
            var panel = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = name, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = $"Type: {typeLabel}" }
                }
            };

            foreach (var (k, v) in ToolTipLines)
                panel.Children.Add(new TextBlock { Text = $"{k}: {v}" });

            return panel;
        };
    }
}