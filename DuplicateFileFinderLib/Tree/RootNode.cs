namespace DuplicateFileFinderLib.Tree;

public class RootNode() : FolderNode("ROOT", DateTimeOffset.Now)
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
}