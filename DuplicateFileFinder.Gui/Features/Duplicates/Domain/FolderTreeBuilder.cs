using System.Collections.ObjectModel;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Domain;

public sealed class FolderTreeBuilder
{
    // parentDirId -> children
    private readonly Dictionary<long, List<long>> _childDirIdsByParent = new();

    // current snapshot dir lookup
    private readonly Dictionary<long, DirRecord> _dirs = new();

    // dirId -> node
    private readonly Dictionary<long, FolderNodeViewModel> _folderNodes = new();
    private readonly IRepo _repo;
    private readonly IScanCoordinator _scanner;

    public FolderTreeBuilder(IRepo repo, IScanCoordinator scanner)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    }

    public void Rebuild(IRepoView snapshot, ObservableCollection<FolderNodeViewModel> folderRoots)
    {
        folderRoots.Clear();
        _dirs.Clear();
        _folderNodes.Clear();
        _childDirIdsByParent.Clear();

        foreach (var (id, dir) in snapshot.Dirs)
            _dirs[id] = dir;

        // Build parent -> children, only for live directories
        foreach (var dir in _dirs.Values)
        {
            if (dir.Status == ScanEntryStatus.None)
                continue;

            if (dir.ParentDirId is { } parentId &&
                _dirs.TryGetValue(parentId, out var parentDir) &&
                parentDir.Status != ScanEntryStatus.None)
            {
                if (!_childDirIdsByParent.TryGetValue(parentId, out var list))
                {
                    list = [];
                    _childDirIdsByParent[parentId] = list;
                }

                list.Add(dir.DirId);
            }
        }

        // Visible roots = scan roots
        foreach (var scanRoot in _repo.ScanRootsView.Where(r => !r.IsDeleted))
        {
            if (!_dirs.TryGetValue(scanRoot.DirId, out var rootDir))
                continue;

            if (rootDir.Status == ScanEntryStatus.None)
                continue;

            var node = GetOrCreateNode(rootDir.DirId, true);

            node.Parent = null;
            node.ShowFullPath = true;
            node.OnRootRemoved = n => folderRoots.Remove(n);
            node.ScanRootId = scanRoot.RootId;

            InsertRootSorted(folderRoots, node);
        }
    }

    private FolderNodeViewModel GetOrCreateNode(long dirId, bool isScanRoot)
    {
        if (_folderNodes.TryGetValue(dirId, out var existing))
            return existing;

        var dir = _dirs[dirId];

        string fullPath;
        if (isScanRoot)
        {
            var scanRoot = _repo.ScanRootsView.FirstOrDefault(s => s.DirId == dirId);
            if (scanRoot != null)
                fullPath = scanRoot.VolumePath != null
                    ? Path.Combine(scanRoot.VolumePath, scanRoot.RootPath)
                    : scanRoot.RootPath;
            else
                fullPath = _repo.GetDirPath(dir.DirId);
        }
        else
        {
            fullPath = _repo.GetDirPath(dir.DirId);
        }

        var node = new FolderNodeViewModel(dir.DirId, dir.Name, fullPath, _scanner)
        {
            EnsureChildrenLoaded = EnsureChildrenLoaded
        };

        _folderNodes[dirId] = node;

        if (_childDirIdsByParent.ContainsKey(dir.DirId))
            node.AddDummyChild();

        return node;
    }

    private void EnsureChildrenLoaded(FolderNodeViewModel node)
    {
        if (!node.HasDummyChild)
            return;

        node.ClearChildren();

        if (!_childDirIdsByParent.TryGetValue(node.DirId, out var childIds))
            return;

        foreach (var childId in childIds)
        {
            var childNode = GetOrCreateNode(childId, false);
            childNode.Parent = node;
            InsertChildSorted(node, childNode);
        }
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