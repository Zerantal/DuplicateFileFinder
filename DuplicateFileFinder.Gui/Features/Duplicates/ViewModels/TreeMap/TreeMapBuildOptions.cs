namespace DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

public sealed record TreeMapBuildOptions
{
    /// <summary>Depth relative to each scan-root (0 = scan-root itself).</summary>
    public int MaxDepth { get; init; } = 6;

    /// <summary>Keep top N subdirs (by selected metric) per directory; rest go into "Other dirs".</summary>
    public int MaxSubdirsPerDir { get; init; } = 32;

    /// <summary>Keep top M files (by size) per directory; rest go into "Other files".</summary>
    public int MaxFilesPerDir { get; init; } = 64;

    /// <summary>Skip file rectangles entirely (directory-only treemap).</summary>
    public bool DirectoriesOnly { get; init; } = false;

    public int MaxTotalFileNodes { get; init; } = int.MaxValue;

    public static TreeMapBuildOptions Default => new();
}