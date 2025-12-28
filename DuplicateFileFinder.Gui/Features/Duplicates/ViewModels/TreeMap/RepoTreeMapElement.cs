using Avalonia.Controls;
using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinderLib.Repository.Storage.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public abstract class RepoTreeMapElement(ITreeMapDataResolver resolver) : ITreeMapNodeElement
{
    protected ITreeMapDataResolver Resolver { get; } = resolver;

    // Treemap layout needs this eagerly.
    public required double Value { get; init; }

    // Not used visually; keep non-null for interface compatibility.
    public virtual string Label => string.Empty;

    protected ScanRoot? ScanRoot { get; init; }
    public string VolumeLabel => ScanRoot?.VolumeLabel ?? "(unknown)";

    public string Name => field ??= ResolveName();

    public string RelativePath => field ??= ResolveRelativePath();

    // Keep as-is (can later be cached if it shows up in profiling)
    public Func<Control> ToolTipFactory => () => new ContentControl { Content = this };

    protected abstract string ResolveName();
    protected abstract string ResolveRelativePath();
}
