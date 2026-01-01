using System.Collections.ObjectModel;

using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Infrastructure.Services;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Domain;

public sealed class FolderTreeBuilder(IRepoHost? repoHost, IScanCoordinator scanner, IDialogService dialogService)
{
    // dirId -> node (assumes DirId is globally unique in repo; if not, key by DirHandle instead)
    private readonly Dictionary<long, FolderNodeViewModel> _folderNodes = new();

    private readonly IRepo _repo = repoHost?.Repo ?? throw new ArgumentNullException(nameof(repoHost));
    private readonly ITreeIndexReadModel _treeIndex = repoHost.TreeIndex ?? throw new ArgumentNullException(nameof(repoHost));
    private readonly IFileDirReadModel _mainIndex = repoHost.FileDirIndex ?? throw new ArgumentNullException(nameof(repoHost));
    private readonly IScanCoordinator _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    private readonly IDialogService _dialogs = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

    // Current snapshot used by lazy loading
    private RepoSnapshotView? _snapshot;

    // Map scanRoot.DirId -> scanRoot info (for root full path)
    private Dictionary<long, ScanRootViewEntry> _scanRootByDirId = new();

    private sealed record ScanRootViewEntry(
        string RootPath,
        string? VolumePath,
        string? VolumeLabel,
        string? DisplayName);

    public void Rebuild(RepoSnapshotView snapshot, ObservableCollection<FolderNodeViewModel> folderRoots)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        folderRoots.Clear();
        _folderNodes.Clear();

        // Capture current scan roots for quick lookup when building root labels
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

            var node = CreateRootNode(rootHandle, label, scanRootFullPath, folderRoots, scanRoot.RootId);

            // Dummy child if it has subdirs (lazy load)
            if (HasChildDirs(rootHandle))
                node.AddDummyChild();

            InsertRootSorted(folderRoots, node);
        }
    }

    private FolderNodeViewModel CreateRootNode(
        DirHandle rootHandle,
        string label,
        string fullPath,
        ObservableCollection<FolderNodeViewModel> folderRoots,
        long scanRootId)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Rebuild must be called before building nodes.");

        var dirRec = _snapshot.GetDirRecord(rootHandle);
        var dirId = dirRec.DirId;

        // Root node uses label rules; do not reuse GetOrCreateNode naming for roots
        var node = new FolderNodeViewModel(dirId, label, fullPath, _scanner, _dialogs, scanRootId: scanRootId)
        {
            EnsureChildrenLoaded = EnsureChildrenLoaded
        };

        _folderNodes[dirId] = node;

        node.Parent = null;
        node.ShowFullPath = false; // label already includes path when needed
        node.OnRootRemoved = n => folderRoots.Remove(n);

        node.OnRootLabelRefreshRequested = () =>
        {
            // Rebuild the entire root list (cheap compared to keeping subtle state in sync)
            var snap = _repo.GetRepoSnapshotView();
            Rebuild(snap, folderRoots);
        };

        return node;
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
            return BuildFullPath(h);

        return rootDirId.ToString();
    }

    private FolderNodeViewModel GetOrCreateNode(DirHandle dirHandle, string parentPath)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Rebuild must be called before building nodes.");

        var dirRec = _snapshot.GetDirRecord(dirHandle);
        var dirId = dirRec.DirId;

        if (_folderNodes.TryGetValue(dirId, out var existing))
            return existing;

        var name = _snapshot.DecodeDirName(dirHandle);
        var fullPath = Path.Combine(parentPath, name);

        var node = new FolderNodeViewModel(dirId, name, fullPath, _scanner)
        {
            EnsureChildrenLoaded = EnsureChildrenLoaded
        };

        _folderNodes[dirId] = node;

        // Dummy child if it has subdirs (lazy load)
        if (HasChildDirs(dirHandle))
            node.AddDummyChild();

        return node;
    }

    private bool HasChildDirs(DirHandle dirHandle) => _treeIndex.GetChildDirs(dirHandle).Length > 0;

    private void EnsureChildrenLoaded(FolderNodeViewModel node)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Rebuild must be called before expanding nodes.");

        if (!node.HasDummyChild)
            return;

        node.ClearChildren();

        if (!_mainIndex.TryGetDir(node.DirId, out var parentHandle))
            return;

        var childHandles = _treeIndex.GetChildDirs(parentHandle);
        foreach (var childHandle in childHandles)
        {
            // Filter out deleted/dummy child dirs so they never appear in the tree.
            var childRec = _snapshot.GetDirRecord(childHandle);
            if (childRec.Status is ScanEntryStatus.None or ScanEntryStatus.Deleted)
                continue;

            var childNode = GetOrCreateNode(childHandle, node.FullPath);
            childNode.Parent = node;
            childNode.ShowFullPath = false;
            InsertChildSorted(node, childNode);
        }
    }

    private string BuildFullPath(DirHandle leaf)
    {
        if (_snapshot is null)
            throw new InvalidOperationException("Rebuild must be called before building paths.");

        _mainIndex.TryGetDirPathByHandle(leaf, out var relativePath);
        return Path.Combine(relativePath);
    }

    private static void InsertRootSorted(ObservableCollection<FolderNodeViewModel> roots, FolderNodeViewModel node)
    {
        var index = 0;
        while (index < roots.Count &&
               string.Compare(roots[index].Name, node.Name, StringComparison.OrdinalIgnoreCase) < 0)
            index++;
        roots.Insert(index, node);
    }

    private static void InsertChildSorted(FolderNodeViewModel parent, FolderNodeViewModel node)
    {
        var children = parent.Children;
        var index = 0;
        while (index < children.Count &&
               string.Compare(children[index].Name, node.Name, StringComparison.OrdinalIgnoreCase) < 0)
            index++;
        children.Insert(index, node);
    }
}
