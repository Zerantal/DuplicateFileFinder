// DuplicateFileFinderLib/Tree/TreePromoter.cs

using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Tree;

public static class TreePromoter
{
    private static FolderNode EnsureFolderNode(FolderNode ancestorNode, string targetFolderPath)
    {
        var rel = Path.GetRelativePath(ancestorNode.Path, targetFolderPath);
        if (rel == "." || rel == string.Empty) return ancestorNode;

        var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var cursor = ancestorNode;
        var currentPath = ancestorNode.Path;

        foreach (var part in parts)
        {
            currentPath = Path.Combine(currentPath, part);
            var existing = cursor.FindSubFolderByPath(currentPath);
            if (existing is null)
            {
                existing = new FolderNode(currentPath);
                cursor.AddFileSystemNode(existing);
            }

            cursor = existing;
        }

        return cursor;
    }

    public static RootNode PromoteAncestor(RootNode root, string ancestorPath)
    {
        var promoted = new RootNode();
        foreach (var r in root.SubFolders)
            promoted.AddFileSystemNode(r.DeepCloneSubtree());
        
        var descendants = promoted.SubFolders.Where(r => PathUtils.IsAncestorOfPath(ancestorPath, r.Path)).ToList();
        if (descendants.Count == 0) return root;

        var ancestorNode = new FolderNode(ancestorPath);

        foreach (var r in descendants)
        {
            promoted.RemoveChild(r);
            var parentOfR = Path.GetDirectoryName(r.Path) ?? ancestorPath;
            var attachParent = EnsureFolderNode(ancestorNode, parentOfR);
            attachParent.AddFileSystemNode(r);
        }

        promoted.AddFileSystemNode(ancestorNode);
        return promoted;
    }
}