using System.Reflection.Metadata;
using DuplicateFileFinderLib.Tree;
using DuplicateFileFinderLib.Util;
using NLog.LayoutRenderers;

namespace DuplicateFileFinderLib.Tree;

public class RootNode() : FolderNode("ROOT")
{
    protected override void WriteCsvEntry(TextWriter writer)
    {
        //NOOP
    }

    public override void UpdateFolderStats()
    {
        base.UpdateFolderStats();

        if (AggregateFolderCount > 0)
            AggregateFolderCount -= 1;
    }
    
    public override void AddFileSystemNode(FileSystemNode node)
    {
        if (node is FolderNode)
            base.AddFileSystemNode(node);
        else
            throw new InvalidOperationException("Can only add FolderNode to RootNode");
    }
    
    public void ReplaceChildInRoot(FolderNode replacement)
    {
        var existing = children.FirstOrDefault(f => PathUtils.IsSamePath(f.Path, replacement.Path));
        if (existing is not null)
            RemoveChild(existing);
        
        AddFileSystemNode(replacement);
    }
}