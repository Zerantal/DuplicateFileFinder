using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

using DuplicateFileFinderLib.Repository.Core.Models;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Application;

public static class DuplicateSelectionTranslator
{
    public static DuplicateExplorerSelectionContext.SelectionTarget? FromTreeRow(
        RepoSnapshotView snapshot,
        ScanRootsRowViewModel? row)
    {
        if (row?.Dir is not { IsValid: true } dirHandle)
            return null;

        var rec = snapshot.GetDirRecord(dirHandle);
        var parentDirId = rec.ParentDirId >= 0 ? (DirId?)rec.ParentDirId : null;

        return DuplicateExplorerSelectionContext.SelectionTarget.ForDirectory(rec.DirId, parentDirId);
    }

    public static DuplicateExplorerSelectionContext.SelectionTarget? FromTreeMapNode(
        RepoSnapshotView snapshot,
        TreeMapNode<ITreeMapNodeElement>? node)
    {
        if (node?.Element is null)
            return null;

        switch (node.Element)
        {
            case DirTreeMapElement dirElement:
                {
                    var rec = snapshot.GetDirRecord(dirElement.Dir);
                    var parentDirId = rec.ParentDirId >= 0 ? (DirId?)rec.ParentDirId : null;
                    return DuplicateExplorerSelectionContext.SelectionTarget.ForDirectory(rec.DirId, parentDirId);
                }

            case FileTreeMapElement fileElement:
                {
                    var rec = snapshot.GetFileRecord(fileElement.File);
                    var parentDirId = rec.DirId >= 0 ? (DirId?)rec.DirId : null;
                    return DuplicateExplorerSelectionContext.SelectionTarget.ForFile(rec.FileId, parentDirId);
                }

            case SyntheticTreeMapElement { ParentDir: { } parentDir }:
                {
                    var rec = snapshot.GetDirRecord(parentDir);
                    return DuplicateExplorerSelectionContext.SelectionTarget.ForSyntheticDirectoryBucket(rec.DirId);
                }

            default:
                return null;
        }
    }

    public static DirId? GetDesiredTreeDirectory(
        DuplicateExplorerSelectionContext.SelectionTarget? target)
    {
        if (target is null)
            return null;

        return target.Value.Kind switch
        {
            DuplicateExplorerSelectionContext.SelectionKind.Directory => target.Value.DirId,
            DuplicateExplorerSelectionContext.SelectionKind.File => target.Value.ParentDirId,
            DuplicateExplorerSelectionContext.SelectionKind.SyntheticDirectoryBucket => target.Value.ParentDirId,
            _ => null
        };
    }
}
