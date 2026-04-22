using System.Collections.Immutable;

using DuplicateFileFinder.Gui.Controls.TreeMap;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.ScanRootsTree;
using DuplicateFileFinder.Gui.Features.Duplicates.ViewModels.TreeMap;

using DuplicateFileFinderLib.Repository.Core.Models;
using DuplicateFileFinderLib.Repository.Plugins.Interfaces;

namespace DuplicateFileFinder.Gui.Features.Duplicates.Application;

public static class DuplicateSelectionTranslator
{
    public static DuplicateExplorerSelectionContext.SelectionTarget? FromTreeRow(
        RepoSnapshotView snapshot,
        IFileDirReadModel fileDirIndex,
        ScanRootsRowViewModel? row)
    {
        if (row?.Dir is not { IsValid: true } dirHandle)
            return null;

        var chain = BuildDirectoryChain(snapshot, fileDirIndex, dirHandle);
        return chain.Length == 0
            ? null
            : DuplicateExplorerSelectionContext.SelectionTarget.ForDirectory(chain);
    }

    public static DuplicateExplorerSelectionContext.SelectionTarget? FromTreeMapNode(
        RepoSnapshotView snapshot,
        IFileDirReadModel fileDirIndex,
        TreeMapNode<ITreeMapNodeElement>? node)
    {
        if (node?.Element is null)
            return null;

        switch (node.Element)
        {
            case DirTreeMapElement dirElement:
                {
                    var chain = BuildDirectoryChain(snapshot, fileDirIndex, dirElement.Dir);
                    return chain.Length == 0
                        ? null
                        : DuplicateExplorerSelectionContext.SelectionTarget.ForDirectory(chain);
                }

            case FileTreeMapElement fileElement:
                {
                    var fileRec = snapshot.GetFileRecord(fileElement.File);

                    if (!fileDirIndex.TryGetDir(fileRec.DirId, out var parentDirHandle))
                        return null;

                    var chain = BuildDirectoryChain(snapshot, fileDirIndex, parentDirHandle);
                    return chain.Length == 0
                        ? null
                        : DuplicateExplorerSelectionContext.SelectionTarget.ForFile(fileRec.FileId, chain);
                }

            case SyntheticTreeMapElement { ParentDir: { } parentDir }:
                {
                    var chain = BuildDirectoryChain(snapshot, fileDirIndex, parentDir);
                    return chain.Length == 0
                        ? null
                        : DuplicateExplorerSelectionContext.SelectionTarget.ForSyntheticDirectoryBucket(chain);
                }

            default:
                return null;
        }
    }



    private static ImmutableArray<DirId> BuildDirectoryChain(
        RepoSnapshotView snapshot,
        IFileDirReadModel fileDirIndex,
        DirHandle dirHandle)
    {
        var builder = ImmutableArray.CreateBuilder<DirId>();
        var current = dirHandle;

        while (current.IsValid)
        {
            var rec = snapshot.GetDirRecord(current);
            builder.Add(rec.DirId);

            if (rec.ParentDirId < 0)
                break;

            if (!fileDirIndex.TryGetDir(rec.ParentDirId, out current))
                break;
        }

        builder.Reverse();
        return builder.ToImmutable();
    }
}
