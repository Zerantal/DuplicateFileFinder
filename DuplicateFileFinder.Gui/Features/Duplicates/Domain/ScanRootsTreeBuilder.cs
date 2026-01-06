using System.Collections.ObjectModel;

using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Domain;

/// <summary>
/// Builds a scan-root directory tree (WinDirStat-like), with aggregate stats pulled from TreeIndex stats.
/// Supports lazy materialization and navigation by ancestry expansion.
/// </summary>
public sealed class ScanRootsTreeBuilder(IRepoHost host, IScanCoordinator scanner, IDialogService dialogService)
{
    private readonly Dictionary<DirHandle, FolderNodeViewModel> _nodesByDirHandle = new();

    private readonly IRepo _repo = host.Repo ?? throw new ArgumentNullException(nameof(host));
    private readonly ITreeIndexReadModel _treeIndex = host.TreeIndex ?? throw new ArgumentNullException(nameof(host));
    private readonly IFileDirReadModel _mainIndex = host.FileDirIndex ?? throw new ArgumentNullException(nameof(host));
    private readonly IScanCoordinator _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    private readonly IDialogService _dialogs = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

    private RepoSnapshotView? _snapshot;

    // dirId (of scanroot dir) -> scanroot display info
    private Dictionary<long, ScanRootViewEntry> _scanRootByDirId = new();

    private sealed record ScanRootViewEntry(
        string RootPath,
        string? VolumePath,
        string? VolumeLabel,
        string? DisplayName);

    public void Rebuild(RepoSnapshotView snapshot, ObservableCollection<FolderNodeViewModel> roots)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        roots.Clear();
        _nodesByDirHandle.Clear();

        _scanRootByDirId = _repo.ScanRootsView
            .Where(r => !r.IsDeleted)
            .ToDictionary(
                r => r.DirId,
                r => new ScanRootViewEntry(r.RootPath, r.VolumePath, r.VolumeLabel, r.DisplayName));

        foreach (var scanRoot in _repo.ScanRootsView.Where(r => !r.IsDeleted))
        {
            if (!_mainIndex.TryGetDir(scanRoot.DirId, out var rootHandle))
                continue;

            var rootRec = snapshot.GetDirRecord(rootHandle);
            if (rootRec.Status is ScanEntryStatus.None or ScanEntryStatus.Deleted)
                continue;

            var scanRootFullPath = GetScanRootFullPath(scanRoot.DirId);
            var label = GetScanRootLabel(scanRoot.DirId);

            var rootNode = CreateRootNode(rootHandle, label, scanRootFullPath, roots, scanRoot.RootId);

            // Stats for root from index
            var rootStats = _treeIndex.GetDirStats(rootHandle);
            rootNode.ApplyAggregateStats(rootStats, scanRootTotalBytes: rootStats.TotalBytes);

            // Dummy child if it has subdirs (lazy load)
            if (HasChildDirs(rootHandle))
                rootNode.AddDummyChild();

            InsertSortedBySizeDesc(roots, rootNode);
        }
    }

    public bool TryGetNode(DirHandle dirHandle, out FolderNodeViewModel node)
        => _nodesByDirHandle.TryGetValue(dirHandle, out node!);

    public bool TryGetDirHandle(long dirId, out DirHandle handle)
        => _mainIndex.TryGetDir(dirId, out handle);

    public bool TryGetParentDirHandle(FileHandle fileHandle, out DirHandle parentDirHandle)
    {
        parentDirHandle = default;

        if (_snapshot is null)
            return false;

        var fileRec = _snapshot.GetFileRecord(fileHandle);

        if (!_mainIndex.TryGetDir(fileRec.DirId, out parentDirHandle))
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

            // NOTE: assumes DirRecord has ParentDirId. If yours differs, adjust here.
            var parentDirId = curRec.ParentDirId;
            if (parentDirId <= 0)
                return false;

            if (!_mainIndex.TryGetDir(parentDirId, out var parentHandle))
                return false;

            current = parentHandle;
        }

        chainRootToTarget.Reverse(); // scanroot -> ... -> target
        return true;
    }

    public FolderNodeViewModel EnsureNodeExistsUnderParent(
        FolderNodeViewModel parentNode,
        DirHandle childHandle)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Rebuild must be called first.");

        // Ensure parent children are loaded (no-op if already loaded).
        EnsureChildrenLoaded(parentNode);

        // If the child VM already exists (loaded or created earlier), return it.
        if (_nodesByDirHandle.TryGetValue(childHandle, out var existing))
            return existing;

        // Otherwise create just this child and attach it to the parent.
        var childNode = GetOrCreateNode(
            childHandle,
            parentNode.FullPath,
            parentNode.ScanRootTotalBytes,
            parentNode.ScanRootId);

        childNode.Parent = parentNode;
        childNode.ShowFullPath = false;

        InsertSortedBySizeDesc(parentNode.Children, childNode);

        return childNode;
    }

    private FolderNodeViewModel CreateRootNode(
        DirHandle rootHandle,
        string label,
        string fullPath,
        ObservableCollection<FolderNodeViewModel> roots,
        long scanRootId)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Rebuild must be called first.");

        var node = new FolderNodeViewModel(
            rootHandle,
            name: label,
            fullPath: fullPath,
            scanCoordinator: _scanner,
            dialogs: _dialogs,
            scanRootId: scanRootId)
        {
            EnsureChildrenLoaded = EnsureChildrenLoaded
        };

        _nodesByDirHandle[rootHandle] = node;

        node.Parent = null;
        node.ShowFullPath = false;
        node.OnRootRemoved = n => roots.Remove(n);

        node.OnRootLabelRefreshRequested = () =>
        {
            var snap = _repo.GetRepoSnapshotView();
            Rebuild(snap, roots);
        };

        return node;
    }

    private FolderNodeViewModel GetOrCreateNode(
        DirHandle dirHandle,
        string parentPath,
        long scanRootTotalBytes,
        long scanRootId)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Rebuild must be called first.");

        if (_nodesByDirHandle.TryGetValue(dirHandle, out var existing))
            return existing;

        var name = _snapshot.DecodeDirName(dirHandle);
        var fullPath = Path.Combine(parentPath, name);

        var node = new FolderNodeViewModel(
            dir: dirHandle,
            name: name,
            fullPath: fullPath,
            scanCoordinator: _scanner,
            dialogs: _dialogs,
            scanRootId: scanRootId)
        {
            EnsureChildrenLoaded = EnsureChildrenLoaded
        };

        _nodesByDirHandle[dirHandle] = node;

        // Stats from index
        var stats = _treeIndex.GetDirStats(dirHandle);
        node.ApplyAggregateStats(stats, scanRootTotalBytes);

        if (HasChildDirs(dirHandle))
            node.AddDummyChild();

        return node;
    }

    private bool HasChildDirs(DirHandle dirHandle) => _treeIndex.GetChildDirs(dirHandle).Length > 0;

    private void EnsureChildrenLoaded(FolderNodeViewModel node)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Rebuild must be called first.");

        if (!node.HasDummyChild)
            return;

        node.ClearChildren();

        // For percent calculation, every node uses the scan-root total bytes
        var scanRootTotal = node.ScanRootTotalBytes;

        var childHandles = _treeIndex.GetChildDirs(node.Dir);
        foreach (var childHandle in childHandles)
        {
            var childRec = _snapshot.GetDirRecord(childHandle);
            if (childRec.Status is ScanEntryStatus.None or ScanEntryStatus.Deleted)
                continue;

            var childNode = GetOrCreateNode(childHandle, node.FullPath, scanRootTotal, node.ScanRootId);
            childNode.Parent = node;
            childNode.ShowFullPath = false;

            InsertSortedBySizeDesc(node.Children, childNode);
        }
    }

    private string GetScanRootLabel(long rootDirId)
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

    private string GetScanRootFullPath(long rootDirId)
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
        if (_mainIndex.TryGetDir(rootDirId, out var h))
        {
            _mainIndex.TryGetDirPathByHandle(h, out var relativePath);
            return Path.Combine(relativePath);
        }

        return rootDirId.ToString();
    }

    private static void InsertSortedBySizeDesc(ObservableCollection<FolderNodeViewModel> list, FolderNodeViewModel node)
    {
        var i = 0;
        while (i < list.Count && list[i].TotalBytes > node.TotalBytes)
            i++;
        list.Insert(i, node);
    }
}
