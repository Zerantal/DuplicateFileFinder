﻿namespace DuplicateFileFinderLib;

public class RootNode : FolderNode
{
    public RootNode() : base("ROOT")
    {
    }

    protected override void WriteCsvEntry(TextWriter writer)
    {
        //NOOP
    }

    public override void AddFileSystemNode(FileSystemNode node)
    {
        if (node is FolderNode)
            base.AddFileSystemNode(node);
        else
        {
            throw new InvalidOperationException("Can only add FolderNode to RootNode");
        }
    }
}