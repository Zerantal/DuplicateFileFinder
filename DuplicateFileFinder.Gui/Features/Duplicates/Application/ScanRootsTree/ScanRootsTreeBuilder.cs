// DuplicateFileFinder.Gui/Features/Duplicates/Application/ScanRootsTree/ScanRootsTreeBuilder.cs

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Storage.Models;

using NLog;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Application.ScanRootsTree;

/// <summary>
///     Builds a scan-root directory tree as UI-agnostic node models.
///     Supports lazy materialization and navigation by ancestry expansion.
///     the builder returns ScanRootsTreeNode models and holds lookup state.
///     ScanRootsTreeViewModel is responsible for turning nodes into VMs (ScanRootsTreeView.
/// </summary>
public sealed class ScanRootsTreeBuilder(IRepoHost host)
{
    private static readonly Logger s_log = LogManager.GetCurrentClassLogger();

    private readonly IRepoHost _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly IRepo _repo = host.Repo ?? throw new ArgumentNullException(nameof(host));

    private readonly Dictionary<DirHandle, ScanRootsTreeNode> _nodesByDirHandle = new();
    private ITreeIndexReadModel TreeIndex => _host.TreeIndex;
    private IFileDirReadModel MainIndex => _host.FileDirIndex;

    // dirId (of scanroot dir) -> scanroot display info
    private Dictionary<DirId, ScanRootViewEntry> _scanRootByDirId = new();

    private RepoSnapshotView? _snapshot;

    /// <summary>
    ///     Rebuilds and returns the root node models (one per scan root).
    /// </summary>
    public List<ScanRootsTreeNode> Build(RepoSnapshotView snapshot)
    {
        s_log.Info("Building scan-root tree (lastIndexedGeneration={0})...",
            host.LastIndexedGeneration );

        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        _nodesByDirHandle.Clear();

        _scanRootByDirId = _repo.ScanRootsView
            .Where(r => !r.IsDeleted)
            .ToDictionary(
                r => r.DirId,
                r => new ScanRootViewEntry(r.RootPath, r.VolumePath, r.VolumeLabel, r.DisplayName));

        var roots = new List<ScanRootsTreeNode>();

        foreach (var scanRoot in _repo.ScanRootsView.Where(r => !r.IsDeleted))
        {
            var scanRootFullPath = GetScanRootFullPath(scanRoot.DirId);
            var label = GetScanRootLabel(scanRoot.DirId);

            // Derive status tag from checkpoint or last run
            var hasCheckpoint = _repo.HasScanCheckpoint(scanRoot.RootId);
            var lastRun = _repo.ScanRunsView
                .Where(r => r.ScanRootId == scanRoot.RootId)
                .OrderByDescending(r => r.ScanSequence)
                .FirstOrDefault();
            var statusTag = GetStatusTag(hasCheckpoint, lastRun);

            if (!string.IsNullOrWhiteSpace(statusTag))
                label = $"{label} {statusTag}";

            // Create root (resolvable handle => normal; otherwise placeholder)
            var rootNode = CreateRootNodeModel(
                scanRoot.DirId,
                label,
                scanRootFullPath,
                scanRoot.RootId,
                hasCheckpoint,
                out var rootHandle);

            // If we don't have a resolvable handle (or it isn't usable in snapshot), it's a placeholder.
            if (!rootHandle.IsValid)
            {
                InsertSortedBySizeDesc(roots, rootNode);
                continue;
            }

            // Stats for root from index
            var rootStats = TreeIndex.GetDirStats(rootHandle);
            rootNode.ApplyAggregateStats(rootStats, rootStats.TotalBytes);

            // Mark lazy children support
            rootNode.HasLazyChildren = HasChildDirs(rootHandle);

            InsertSortedBySizeDesc(roots, rootNode);
        }

        return roots;
    }

    public bool TryGetNode(DirHandle dirHandle, out ScanRootsTreeNode node)
        => _nodesByDirHandle.TryGetValue(dirHandle, out node!);

    public bool TryGetDirHandle(DirId dirId, out DirHandle handle)
        => MainIndex.TryGetDir(dirId, out handle);

    public bool TryGetParentDirHandle(FileHandle fileHandle, out DirHandle parentDirHandle)
    {
        parentDirHandle = DirHandle.Invalid;

        if (_snapshot is null)
            return false;

        var fileRec = _snapshot.GetFileRecord(fileHandle);

        if (!MainIndex.TryGetDir(fileRec.DirId, out parentDirHandle))
            return false;

        return true;
    }

    public bool TryBuildAncestorChainToScanRoot(DirHandle target, out List<DirHandle> chainRootToTarget)
    {
        chainRootToTarget = new List<DirHandle>(32);

        if (_snapshot is null)
            return false;

        // Walk upward by ParentDirId until we hit a scan-root dirId.
        var current = target;

        while (true)
        {
            chainRootToTarget.Add(current);

            var curRec = _snapshot.GetDirRecord(current);
            var curDirId = curRec.DirId;

            if (_scanRootByDirId.ContainsKey(curDirId))
                break;

            var parentDirId = curRec.ParentDirId;
            if (parentDirId <= 0)
                return false;

            if (!MainIndex.TryGetDir(parentDirId, out var parentHandle))
                return false;

            current = parentHandle;
        }

        chainRootToTarget.Reverse(); // scanroot -> ... -> target
        return true;
    }

    /// <summary>
    ///     Ensures a child node model exists under a given parent model.
    ///     This does NOT create viewmodels; it only materializes the model tree.
    /// </summary>
    public ScanRootsTreeNode EnsureNodeExistsUnderParent(
        ScanRootsTreeNode parentNode,
        DirHandle childHandle)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Build must be called first.");

        EnsureChildrenLoaded(parentNode);

        // Already created anywhere in the tree?
        if (_nodesByDirHandle.TryGetValue(childHandle, out var existing))
            return existing;

        // Create and attach to parent
        var childNode = GetOrCreateNodeModel(
            childHandle,
            parentNode.FullPath,
            parentNode.ScanRootTotalBytes,
            parentNode.ScanRootId);

        childNode.Parent = parentNode;

        InsertSortedBySizeDesc(parentNode.Children, childNode);

        return childNode;
    }

    /// <summary>
    ///     Materializes (once) the children models for a parent model, if it is marked as lazy.
    /// </summary>
    public void EnsureChildrenLoaded(ScanRootsTreeNode node)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Build must be called first.");

        if (!node.HasLazyChildren || node.ChildrenMaterialized)
            return;

        node.Children.Clear();

        var scanRootTotal = node.ScanRootTotalBytes;
        var childHandles = TreeIndex.GetChildDirs(node.Dir);

        var children = new List<ScanRootsTreeNode>(childHandles.Length);

        foreach (var childHandle in childHandles)
        {
            var childRec = _snapshot.GetDirRecord(childHandle);
            if (childRec.Status is ScanEntryStatus.None or ScanEntryStatus.Deleted)
                continue;

            var childNode = GetOrCreateNodeModel(childHandle, node.FullPath, scanRootTotal, node.ScanRootId);
            childNode.Parent = node;
            children.Add(childNode);
        }

        // Sort descending
        children.Sort(static (a, b) => b.TotalBytes.CompareTo(a.TotalBytes));

        // Bulk append
        for (var i = 0; i < children.Count; i++)
            node.Children.Add(children[i]);

        node.ChildrenMaterialized = true;
    }

    // Materialize child node if not already created, but without enumerating siblings
    public bool TryEnsureChildNodeFast(
        ScanRootsTreeNode parentNode,
        DirHandle childHandle,
        out ScanRootsTreeNode childNode)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Build must be called first.");

        childNode = null!;

        if (!childHandle.IsValid)
            return false;

        // Already created anywhere in the tree?
        if (_nodesByDirHandle.TryGetValue(childHandle, out var existing))
        {
            // Ensure parent link if missing (safe)
            existing.Parent ??= parentNode;
            childNode = existing;
            return true;
        }

        // Validate the dir exists in snapshot and isn't deleted
        var childRec = _snapshot.GetDirRecord(childHandle);
        if (childRec.Status is ScanEntryStatus.None or ScanEntryStatus.Deleted)
            return false;

        // Create like GetOrCreateNodeModel, but without enumerating siblings
        var name = _snapshot.DecodeDirName(childHandle);
        var fullPath = Path.Combine(parentNode.FullPath, name);

        var node = new ScanRootsTreeNode
        {
            Dir = childHandle,
            Parent = parentNode,
            ChildrenMaterialized = false,
            HasLazyChildren = false,
            Name = name,
            FullPath = fullPath,
            ScanRootId = parentNode.ScanRootId,
            IsScanRoot = false,
            HasCheckpoint = false,
            IsAvailable = true,
            ScanRootTotalBytes = parentNode.ScanRootTotalBytes
        };

        _nodesByDirHandle[childHandle] = node;

        // Stats from index
        var stats = TreeIndex.GetDirStats(childHandle);
        node.ApplyAggregateStats(stats, parentNode.ScanRootTotalBytes);

        // Has children?
        node.HasLazyChildren = HasChildDirs(childHandle);

        childNode = node;
        return true;
    }

    // ---------------------------------------------------------------------
    // Internal model creation
    // ---------------------------------------------------------------------

    private ScanRootsTreeNode CreateRootNodeModel(
        DirId rootDirId,
        string label,
        string fullPath,
        ScanRootId scanRootId,
        bool hasCheckpoint,
        out DirHandle rootHandle)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Build must be called first.");

        rootHandle = DirHandle.Invalid;

        // Try to resolve the root dir into the current main index.
        if (!MainIndex.TryGetDir(rootDirId, out var handle))
            return CreatePlaceholderRootNodeModel(label, fullPath, scanRootId, hasCheckpoint);

        // If the snapshot record is absent/deleted, treat it as placeholder so it still shows.
        var rootRec = _snapshot.GetDirRecord(handle);
        if (rootRec.Status is ScanEntryStatus.Deleted)
            return CreatePlaceholderRootNodeModel(label, fullPath, scanRootId, hasCheckpoint);

        rootHandle = handle;

        var node = new ScanRootsTreeNode
        {
            Dir = rootHandle,
            Name = label,
            FullPath = fullPath,
            ScanRootId = scanRootId,
            IsScanRoot = true,
            HasCheckpoint = hasCheckpoint,

            // Root percent baseline
            ScanRootTotalBytes = 0, // set by ApplyAggregateStats below
            ChildrenMaterialized = false,
            HasLazyChildren = false
        };

        _nodesByDirHandle[rootHandle] = node;

        return node;
    }

    private static ScanRootsTreeNode CreatePlaceholderRootNodeModel(
        string label,
        string fullPath,
        ScanRootId scanRootId,
        bool hasCheckpoint)
    {
        // Placeholder: Dir invalid, no stats, no children.
        return new ScanRootsTreeNode
        {
            Dir = DirHandle.Invalid,
            Name = label,
            FullPath = fullPath,
            ScanRootId = scanRootId,
            IsScanRoot = true,
            HasCheckpoint = hasCheckpoint,
            ScanRootTotalBytes = 0,
            ChildrenMaterialized = true,
            HasLazyChildren = false
        };
    }

    private ScanRootsTreeNode GetOrCreateNodeModel(
        DirHandle dirHandle,
        string parentPath,
        long scanRootTotalBytes,
        ScanRootId scanRootId)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Build must be called first.");

        if (_nodesByDirHandle.TryGetValue(dirHandle, out var existing))
            return existing;

        var name = _snapshot.DecodeDirName(dirHandle);
        var fullPath = Path.Combine(parentPath, name);

        var node = new ScanRootsTreeNode
        {
            Dir = dirHandle,
            Name = name,
            FullPath = fullPath,
            ScanRootId = scanRootId,
            IsScanRoot = false,
            HasCheckpoint = false,
            ScanRootTotalBytes = scanRootTotalBytes,
            ChildrenMaterialized = false,
            HasLazyChildren = false
        };

        _nodesByDirHandle[dirHandle] = node;

        // Stats from index
        var stats = TreeIndex.GetDirStats(dirHandle);
        node.ApplyAggregateStats(stats, scanRootTotalBytes);

        node.HasLazyChildren = HasChildDirs(dirHandle);

        return node;
    }

    private bool HasChildDirs(DirHandle dirHandle) => TreeIndex.GetChildDirs(dirHandle).Length > 0;

    // ---------------------------------------------------------------------
    // Scan root labeling / status
    // ---------------------------------------------------------------------

    private string GetScanRootLabel(DirId rootDirId)
    {
        var fullPath = GetScanRootFullPath(rootDirId);

        if (_scanRootByDirId.TryGetValue(rootDirId, out var sr))
        {
            if (!string.IsNullOrWhiteSpace(sr.DisplayName))
                return sr.DisplayName!;

            if (!string.IsNullOrWhiteSpace(sr.VolumeLabel))
                return $"{sr.VolumeLabel} [{fullPath}]";
        }

        return fullPath;
    }

    private string GetScanRootFullPath(DirId rootDirId)
    {
        if (_scanRootByDirId.TryGetValue(rootDirId, out var sr))
        {
            var vp = (sr.VolumePath ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var rp = sr.RootPath
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrEmpty(vp))
                return rp;
            if (string.IsNullOrEmpty(rp))
                return vp;
            return Path.Combine(vp, rp);
        }

        // Fallback to index path
        if (MainIndex.TryGetDir(rootDirId, out var h))
        {
            MainIndex.TryGetDirPathByHandle(h, out var relativePath);
            return Path.Combine(relativePath);
        }

        return rootDirId.ToString();
    }

    private static string? GetStatusTag(bool hasCheckpoint, ScanRun? lastRun)
    {
        if (hasCheckpoint)
            return "[INCOMPLETE]";

        return lastRun?.Status switch
        {
            ScanRunStatus.InProgress => "[IN PROGRESS]",
            ScanRunStatus.Failed => "[FAILED]",
            ScanRunStatus.Cancelled => "[CANCELLED]",
            _ => null
        };
    }

    // ---------------------------------------------------------------------
    // Sorting helper (model lists)
    // ---------------------------------------------------------------------

    private static void InsertSortedBySizeDesc(IList<ScanRootsTreeNode> list, ScanRootsTreeNode node)
    {
        var i = 0;
        while (i < list.Count && list[i].TotalBytes > node.TotalBytes)
            i++;
        list.Insert(i, node);
    }

    private sealed record ScanRootViewEntry(
        string RootPath,
        string? VolumePath,
        string? VolumeLabel,
        string? DisplayName);
}
