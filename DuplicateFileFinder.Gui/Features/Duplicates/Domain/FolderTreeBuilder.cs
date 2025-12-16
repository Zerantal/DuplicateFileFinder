using System.Collections.ObjectModel;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels;
using DuplicateFileFinder.Gui.Infrastructure.Services;
using DuplicateFileFinderLib.Repository.Core;
using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

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

    private sealed record ScanRootViewEntry(long RootId, string RootPath, string? VolumePath);

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
                r => new ScanRootViewEntry(r.RootId, r.RootPath, r.VolumePath));

        foreach (var scanRoot in _repo.ScanRootsView.Where(r => !r.IsDeleted))
        {
            if (!_mainIndex.TryGetDir(scanRoot.DirId, out var rootHandle))
                continue;

            var rootRec = snapshot.GetDir(rootHandle);
            if (rootRec.Status == ScanEntryStatus.None)
                continue;
            
            var node = GetOrCreateNode(rootHandle, isScanRoot: true);
        
            node.Parent = null;
            node.ShowFullPath = true;
            node.OnRootRemoved = n => folderRoots.Remove(n);
            node.ScanRootId = scanRoot.RootId;
            
            InsertRootSorted(folderRoots, node);
        }
    }

    private FolderNodeViewModel GetOrCreateNode(DirHandle dirHandle, bool isScanRoot)
                {
        if (_snapshot is null)
            throw new InvalidOperationException("Rebuild must be called before building nodes.");

        var dirRec = _snapshot.GetDir(dirHandle);
        var dirId = dirRec.DirId;

        if (_folderNodes.TryGetValue(dirId, out var existing))
            return existing;

        var name = _snapshot.DecodeDirName(dirHandle);
        var fullPath = isScanRoot
            ? GetScanRootFullPath(dirId)
            : BuildFullPath(dirHandle);

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
        return _treeIndex.GetChildDirIds(dirHandle).Length > 0;
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

        var childHandles = _treeIndex.GetChildDirIds(parentHandle);
        foreach (var childHandle in childHandles)
        {
            var childNode = GetOrCreateNode(childHandle, isScanRoot: false);
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

        // Walk parents via ParentDirId (resolved through FileDirIndex) and decode names via per-root pool.
        // This is only used for nodes you actually materialise (visible nodes), so it’s OK.
        var parts = new List<string>(capacity: 16);

        var cur = leaf;
        while (true)
        {
            var rec = _snapshot.GetDir(cur);
            parts.Add(_snapshot.DecodeDirName(cur));

            if (rec.ParentDirId < 0)
                break;

            if (!_mainIndex.TryGetDir(rec.ParentDirId, out var parent))
                break;

            cur = parent;
        }

        parts.Reverse();
        return Path.Combine(parts.ToArray());
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
