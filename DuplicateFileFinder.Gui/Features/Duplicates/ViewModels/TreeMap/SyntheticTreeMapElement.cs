using System.Diagnostics.CodeAnalysis;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

// Used to hold ScanRoots or as a summary node when total node count becomes excessive
public sealed class SyntheticTreeMapElement : RepoTreeMapElement
{
    private readonly string _label;
    private readonly string _relativePath;

    [SetsRequiredMembers]
    public SyntheticTreeMapElement(
        ITreeMapDataResolver resolver,
        string label,
        double value,
        string typeLabel,
        IReadOnlyList<(string Key, string Value)> lines) : base(resolver)
    {
        _label = label;
        _relativePath = string.Empty;

        ScanRoot = null;
        Value = value;

        TypeLabel = typeLabel;

        // Convert tuples -> KeyValuePair for easy XAML binding
        var list = new List<KeyValuePair<string, string>>(lines.Count);
        foreach (var (k, v) in lines)
            list.Add(new KeyValuePair<string, string>(k, v));
        ToolTipLines = list;
    }

    public string TypeLabel { get; }
    public IReadOnlyList<KeyValuePair<string, string>> ToolTipLines { get; }
    public DirHandle? ParentDir { get; init; }

    protected override string ResolveName() => _label;
    protected override string ResolveRelativePath() => _relativePath;
}
