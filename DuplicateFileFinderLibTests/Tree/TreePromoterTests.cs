using System.IO;
using System.Linq;
using DuplicateFileFinderLib;
using DuplicateFileFinderLib.Tree;
using Xunit;
// ReSharper disable InconsistentNaming

namespace DuplicateFileFinderLibTests.Tree;

public sealed class TreePromoterTests
{
    [Fact]
    public void PromoteAncestor_Reparents_Descendants()
    {
        var root = new RootNode();
        var a_b = new FolderNode(Path.GetFullPath("/tmp/a/b"));
        var a_c = new FolderNode(Path.GetFullPath("/tmp/a/c"));
        root.AddFileSystemNode(a_b);
        root.AddFileSystemNode(a_c);

        var promoted = TreePromoter.PromoteAncestor(root, Path.GetFullPath("/tmp/a"));

        Assert.Contains(promoted.SubFolders, f => f.Path == Path.GetFullPath("/tmp/a"));
        var nodeA = promoted.SubFolders.Single(f => f.Path == Path.GetFullPath("/tmp/a"));

        Assert.Contains(nodeA.SubFolders, f => f.Path == Path.GetFullPath("/tmp/a/b"));
        Assert.Contains(nodeA.SubFolders, f => f.Path == Path.GetFullPath("/tmp/a/c"));
    }

    [Fact]
    public void PromoteAncestor_Idempotent_WhenNoDescendants()
    {
        var root = new RootNode();
        var x = new FolderNode(Path.GetFullPath("/tmp/x"));
        root.AddFileSystemNode(x);

        var result = TreePromoter.PromoteAncestor(root, Path.GetFullPath("/tmp/a"));
        // unchanged
        Assert.Single(result.SubFolders);
        Assert.Equal(x.Path, result.SubFolders[0].Path);
    }
}
