using System.Collections.ObjectModel;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;
using DuplicateFileFinderLib.Repository.Plugins.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Domain;

public sealed class FolderTreeBuilder(IRepoHost? repoHost, IScanCoordinator scanner)
{
    // dirId -> node (assumes DirId is globally unique in repo; if not, key by DirHandle instead)
    private readonly Dictionary<long, FolderNodeViewModel> _folderNodes = new();

    private readonly IRepo _repo = repoHost?.Repo ?? throw new ArgumentNullException(nameof(repoHost));
    private readonly ITreeIndexReadModel _treeIndex = repoHost.TreeIndex ?? throw new ArgumentNullException(nameof(repoHost));
    private readonly IFileDirReadModel _mainIndex = repoHost.FileDirIndex ?? throw new ArgumentNullException(nameof(repoHost));
    private readonly IScanCoordinator _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));

    // Current snapshot used by lazy loading
    private RepoSnapshotView? _snapshot;

    // Map scanRoot.DirId -> scanRoot info (for root full path)
    private Dictionary<long, ScanRootViewEntry> _scanRootByDirId = new();

    private sealed record ScanRootViewEntry(string RootPath, string? VolumePath);

    public void Rebuild(RepoSnapshotView snapshot, ObservableCollection<FolderNodeViewModel> folderRoots)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        folderRoots.Clear();
        _folderNodes.Clear();

        // Capture current scan roots for quick lookup when building full path for scan-root nodes
        _scanRootByDirId = _repo.ScanRootsView
            .Where(r => !r.IsDeleted)
            .ToDictionary(
                r => r.DirId,
                r => new ScanRootViewEntry(r.RootPath, r.VolumePath));

        foreach (var scanRoot in _repo.ScanRootsView.Where(r => !r.IsDeleted))
        {
            if (!_mainIndex.TryGetDir(scanRoot.DirId, out var rootHandle))
                continue;

            var rootRec = snapshot.GetDirRecord(rootHandle);
            if (rootRec.Status is ScanEntryStatus.None or ScanEntryStatus.Deleted)
                continue;

            var scanRootFullPath = GetScanRootFullPath(scanRoot.DirId);
            var node = GetOrCreateNode(rootHandle, scanRootFullPath);

            node.Parent = null;
            node.ShowFullPath = true;
            node.OnRootRemoved = n => folderRoots.Remove(n);
            node.ScanRootId = scanRoot.RootId;

            InsertRootSorted(folderRoots, node);
        }
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

    private bool HasChildDirs(DirHandle dirHandle)
    {
        // Cheap enough for now; if you want, extend TreeIndex with a HasChildren/Count API.
        return _treeIndex.GetChildDirs(dirHandle).Length > 0;
    }

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
            InsertChildSorted(node, childNode);
        }
    }

    private string GetScanRootFullPath(long rootDirId)
    {
        if (_scanRootByDirId.TryGetValue(rootDirId, out var sr))
        {
            return sr.VolumePath != null
                ? Path.Combine(sr.VolumePath, sr.RootPath)
                : sr.RootPath;
        }

        // Fallback: build from parents
        if (_mainIndex.TryGetDir(rootDirId, out var h))
            return BuildFullPath(h);

        return rootDirId.ToString();
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
