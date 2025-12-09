using DuplicateFileFinderLib.Repository.Interfaces;
using DuplicateFileFinderLib.Repository.Models;

namespace DuplicateFileFinderLib.Core;

internal class RemoveRootOperation(IRepoHost host)
{
    public void Execute(long scanRootId)
    {
        var repo = host.Repo;
        var treeIndex = host.TreeIndex;
        
        var scanRoot = repo.ScanRootsView.FirstOrDefault(r => r.RootId == scanRootId);

        if (scanRoot is null)
            return;
        
        var rootDirId = scanRoot.DirId;

        // Traverse subtree using the tree index and build dummy tombstones.
        var dirsToDelete  = new List<DirRecord>();
        var filesToDelete = new List<FileRecord>();

        var stack = new Stack<long>();
        stack.Push(rootDirId);

        while (stack.Count > 0)
        {
            var dirId = stack.Pop();

            // Add dummy dir tombstone (only Id + Status matter for deletion).
            dirsToDelete.Add(new DirRecord
            {
                DirId                = dirId,
                ParentDirId          = null,                // unused for delete
                Name                 = string.Empty,        // unused for delete
                LastSeenScanSequence = 0,                   // unused for delete
                Status               = ScanEntryStatus.Deleted,
                ErrorMessage         = null
            });

            // Files directly under this directory
            foreach (var fileId in treeIndex.GetChildFileIds(dirId))
            {
                filesToDelete.Add(new FileRecord
                {
                    FileId               = fileId,
                    DirId                = dirId,
                    Name                 = string.Empty,       // unused for delete
                    Size                 = 0,
                    Hash                 = default,
                    LastSeenScanSequence = 0,
                    Status               = ScanEntryStatus.Deleted,
                    ErrorMessage         = null
                });
            }

            // Recurse into child directories
            foreach (var childDirId in treeIndex.GetChildDirIds(dirId))
            {
                stack.Push(childDirId);
            }
        }

        if (dirsToDelete.Count == 0 && filesToDelete.Count == 0)
            return;

        // 3. Commit as a synthetic "scan" that consists only of deletions.
        var seq = (repo as IRepoInternal)?.AllocateRunId();
        if (!seq.HasValue) return;

        var delta = new RepoDelta
        {
            ScanSequence = seq.Value,
            Dirs         = dirsToDelete,
            Files        = filesToDelete
        };

        repo.CommitDelta(delta);

        (repo as IRepoInternal)?.DeleteScanRoot(scanRootId);
    }
}