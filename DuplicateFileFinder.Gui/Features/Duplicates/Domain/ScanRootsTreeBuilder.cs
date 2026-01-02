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
/// </summary>
public sealed class ScanRootsTreeBuilder
{
    private readonly Dictionary<long, FolderNodeViewModel> _nodesByDirId = new();

    private readonly IRepo _repo;
    private readonly ITreeIndexReadModel _treeIndex;
    private readonly IFileDirReadModel _mainIndex;
    private readonly IScanCoordinator _scanner;
    private readonly IDialogService _dialogs;

    private RepoSnapshotView? _snapshot;

    // dirId (of scanroot dir) -> scanroot display info
    private Dictionary<long, ScanRootViewEntry> _scanRootByDirId = new();

    private sealed record ScanRootViewEntry(
        string RootPath,
        string? VolumePath,
        string? VolumeLabel,
        string? DisplayName,
        long RootId);

    public ScanRootsTreeBuilder(IRepoHost host, IScanCoordinator scanner, IDialogService dialogService)
    {
        _repo = host.Repo ?? throw new ArgumentNullException(nameof(host));
        _treeIndex = host.TreeIndex ?? throw new ArgumentNullException(nameof(host));
        _mainIndex = host.FileDirIndex ?? throw new ArgumentNullException(nameof(host));

        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _dialogs = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public void Rebuild(RepoSnapshotView snapshot, ObservableCollection<FolderNodeViewModel> roots)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        roots.Clear();
        _nodesByDirId.Clear();

        _scanRootByDirId = _repo.ScanRootsView
            .Where(r => !r.IsDeleted)
            .ToDictionary(
                r => r.DirId,
                r => new ScanRootViewEntry(r.RootPath, r.VolumePath, r.VolumeLabel, r.DisplayName, r.RootId));

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

            InsertRootSorted(roots, rootNode);
        }
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

        var dirRec = _snapshot.GetDirRecord(rootHandle);
        var dirId = dirRec.DirId;

        var node = new FolderNodeViewModel(dirId, label, fullPath, _scanner, _dialogs, scanRootId: scanRootId)
        {
            EnsureChildrenLoaded = EnsureChildrenLoaded
        };

        _nodesByDirId[dirId] = node;

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

    private FolderNodeViewModel GetOrCreateNode(DirHandle dirHandle, string parentPath, long scanRootTotalBytes, long scanRootId)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Rebuild must be called first.");

        var dirRec = _snapshot.GetDirRecord(dirHandle);
        var dirId = dirRec.DirId;

        if (_nodesByDirId.TryGetValue(dirId, out var existing))
            return existing;

        var name = _snapshot.DecodeDirName(dirHandle);
        var fullPath = Path.Combine(parentPath, name);

        var node = new FolderNodeViewModel(dirId, name, fullPath, _scanner, _dialogs, scanRootId: scanRootId)
        {
            EnsureChildrenLoaded = EnsureChildrenLoaded
        };

        _nodesByDirId[dirId] = node;

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

        if (!_mainIndex.TryGetDir(node.DirId, out var parentHandle))
            return;

        // For percent calculation, every node uses the scan-root total bytes
        var scanRootTotal = node.ScanRootTotalBytes;

        var childHandles = _treeIndex.GetChildDirs(parentHandle);
        foreach (var childHandle in childHandles)
        {
            var childRec = _snapshot.GetDirRecord(childHandle);
            if (childRec.Status is ScanEntryStatus.None or ScanEntryStatus.Deleted)
                continue;

            var childNode = GetOrCreateNode(childHandle, node.FullPath, scanRootTotal, node.ScanRootId);
            childNode.Parent = node;
            childNode.ShowFullPath = false;

            InsertChildSorted(node, childNode);
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

    private static void InsertRootSorted(
        ObservableCollection<FolderNodeViewModel> roots,
        FolderNodeViewModel node)
    {
        var index = 0;
        while (index < roots.Count &&
               roots[index].TotalBytes > node.TotalBytes)
        {
            index++;
        }
        roots.Insert(index, node);
    }

    private static void InsertChildSorted(FolderNodeViewModel parent, FolderNodeViewModel node)
    {
        var children = parent.Children;
        var index = 0;

        while (index < children.Count &&
               children[index].TotalBytes > node.TotalBytes)
        {
            index++;
        }

        children.Insert(index, node);
    }

}
