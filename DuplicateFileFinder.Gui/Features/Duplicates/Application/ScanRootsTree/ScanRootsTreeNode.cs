// DuplicateFileFinder.Gui/Features/Duplicates/Application/ScanRootsTree/ScanRootsTreeNode.cs

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;

/// <summary>
/// UI-agnostic node model for the scan-roots tree.
/// Intended to be built by ScanRootsTreeBuilder and then projected to FolderNodeViewModel by a factory.
/// </summary>
public sealed class ScanRootsTreeNode
{
    public required DirHandle Dir { get; init; }

    /// <summary>Null for roots; set by the builder when attaching nodes.</summary>
    public ScanRootsTreeNode? Parent { get; set; }

    /// <summary>Children models. May be empty until materialized (lazy).</summary>
    public List<ScanRootsTreeNode> Children { get; } = new();

    public required long ScanRootId { get; init; }

    /// <summary>True for scan-root nodes (even placeholders); set by builder.</summary>
    public required bool IsScanRoot { get; init; }

    // ---- Display / status info (UI can choose to show path vs name) ----

    public required string Name { get; set; }
    public required string FullPath { get; set; }

    /// <summary>Optional computed status tag e.g. [INCOMPLETE], [FAILED].</summary>
    public string? StatusTag { get; set; }

    /// <summary>Whether the scan root has a checkpoint (used to show "Resume scan").</summary>
    public bool HasCheckpoint { get; set; }

    /// <summary>Used by UI to grey out items if needed.</summary>
    public bool IsAvailable { get; set; } = true;

    // ---- Aggregate stats (same semantics as FolderNodeViewModel) ----

    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    public int DirCount { get; set; }
    public long DuplicateFiles { get; set; }
    public long DuplicateBytes { get; set; }

    public double PercentOfScanRoot { get; set; }
    public long ScanRootTotalBytes { get; set; }

    public int ItemCount => FileCount + DirCount;

    public void ApplyAggregateStats(DirAggregateStats stats, long scanRootTotalBytes)
    {
        TotalBytes = stats.TotalBytes;
        FileCount = stats.FileCount;
        DirCount = stats.DirCount;
        DuplicateFiles = stats.DuplicateFiles;
        DuplicateBytes = stats.DuplicateBytes;

        ScanRootTotalBytes = scanRootTotalBytes <= 0 ? 0 : scanRootTotalBytes;
        PercentOfScanRoot = ScanRootTotalBytes <= 0 ? 0.0 : TotalBytes * 100.0 / ScanRootTotalBytes;
}

    // ---- Lazy materialization flags ----

    /// <summary>
    /// True if this node has children in the index and should be lazily expanded.
    /// </summary>
    public bool HasLazyChildren { get; set; }

    /// <summary>
    /// True once children have been materialized into <see cref="Children"/>.
    /// </summary>
    public bool ChildrenMaterialized { get; set; }
}
