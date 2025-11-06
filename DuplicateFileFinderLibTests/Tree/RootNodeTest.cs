using System;
using System.IO;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Tree;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

namespace DuplicateFileFinderLibTests.Tree;

public class RootNodeTests
{
    // Minimal stub for a non-folder node if you don't already have one in production code.
    // We just need *something* that derives from FileSystemNode but is not a FolderNode.
    private sealed class DummyFileNode(string name) : FileSystemNode(name)
    {
        protected override void WriteCsvEntry(TextWriter writer)
        {
            writer.WriteLine($"FILE,{Path}");
        }
    }

    [Fact]
    public void AddFileSystemNode_Allows_FolderNode()
    {
        // arrange
        var root = new RootNode();
        var childFolder = new FolderNode("child");

        // act
        Exception? ex = Record.Exception(() => root.AddFileSystemNode(childFolder));

        // assert
        Assert.Null(ex); 
                                    
        Assert.Empty(root.Files);
        Assert.Contains(childFolder, root.SubFolders);
    }

    [Fact]
    public void AddFileSystemNode_Rejects_NonFolderNode()
    {
        // arrange
        var root = new RootNode();
        var fileNode = new DummyFileNode("not-a-folder");

        // act
        var ex = Assert.Throws<InvalidOperationException>(
            () => root.AddFileSystemNode(fileNode));

        // assert
        Assert.Equal("Can only add FolderNode to RootNode", ex.Message);

        Assert.Empty(root.SubFolders);
        Assert.Empty(root.Files);
    }

    [Fact]
    public void WriteCsvEntry_IsNoOp_ForRootNode()
    {
        // arrange
        var root = new RootNode();
        using var sw = new StringWriter();

        // act            
        // var probe = new CsvProbeRootNode(root);
        // probe.InvokeWriteCsvEntry(sw);

        root.WriteCsvEntries(sw);
            
        // assert
        Assert.Equal(string.Empty, sw.ToString());
    }
    
    [Fact]
    public void Root_Empty_HasZeroAggregates()
    {
        var root = new RootNode();

        root.UpdateFolderStats();

        Assert.Equal(0, root.AggregateFileCount);
        Assert.Equal(0, root.AggregateFolderCount); // excludes self
        Assert.Equal(0, root.Size);
    }
    
    [Fact]
    public async Task Root_ExcludesSelf_SumsChildrenInclusive()
    {
        var root = new RootNode();
        var a = new FolderNode(PathUtil.AbsPath("/", "R", "A"));
        var b = new FolderNode(PathUtil.AbsPath("/", "R", "B"));

        a.AddFileSystemNode(new FileNode(PathUtil.AbsPath("/", "R", "A", "x.txt"), 1));
        b.AddFileSystemNode(new FileNode(PathUtil.AbsPath("/", "R", "B", "y.txt"), 2));

        root.AddFileSystemNode(a);
        root.AddFileSystemNode(b);

        await root.RecomputeSubtreeAggregatesAsync();

        // Folder nodes include themselves
        Assert.Equal(1, a.AggregateFolderCount);
        Assert.Equal(1, b.AggregateFolderCount);

        // Root excludes itself but sums children (A and B) inclusively
        Assert.Equal(2, root.AggregateFolderCount); // A + B
        Assert.Equal(2, root.AggregateFileCount);   // x + y
        Assert.Equal(3, root.Size);                 // 1 + 2
    }

    [Fact]
    public async Task Root_MultipleLevels_ProducesExpectedCountsAndBytes()
    {
        var root = new RootNode();

        var a  = new FolderNode(PathUtil.AbsPath("/", "R", "A"));
        var a1 = new FolderNode(PathUtil.AbsPath("/", "R", "A", "A1"));
        var b  = new FolderNode(PathUtil.AbsPath("/", "R", "B"));

        a.AddFileSystemNode(new FileNode(PathUtil.AbsPath("/", "R", "A", "fa.bin"), 10));
        a1.AddFileSystemNode(new FileNode(PathUtil.AbsPath("/", "R", "A", "A1", "fa1.bin"), 20));
        a.AddFileSystemNode(a1);

        b.AddFileSystemNode(new FileNode(PathUtil.AbsPath("/", "R", "B", "fb.bin"), 30));

        root.AddFileSystemNode(a);
        root.AddFileSystemNode(b);

        await root.RecomputeSubtreeAggregatesAsync();

        // FolderNodes include self
        Assert.Equal(2, a.AggregateFolderCount);  // A + A1
        Assert.Equal(1, a1.AggregateFolderCount); // A1
        Assert.Equal(1, b.AggregateFolderCount);  // B

        // Root excludes self, sums A and B (including their self-counts)
        Assert.Equal(3, root.AggregateFolderCount); // A, A1, B
        Assert.Equal(3, root.AggregateFileCount);   // fa, fa1, fb
        Assert.Equal(60, root.Size);                // 10 + 20 + 30
    }
    
    [Fact]
    public async Task Root_SingleTopFolder_WorksAsExpected()
    {
        var root = new RootNode();
        var a = new FolderNode(PathUtil.AbsPath("/", "R", "A"));

        a.AddFileSystemNode(new FileNode(PathUtil.AbsPath("/", "R", "A", "x.bin"), 5));
        root.AddFileSystemNode(a);

        await root.RecomputeSubtreeAggregatesAsync();

        // Folder includes self
        Assert.Equal(1, a.AggregateFolderCount);
        Assert.Equal(1, a.AggregateFileCount);
        Assert.Equal(5, a.Size);

        // Root excludes self, shows just A
        Assert.Equal(1, root.AggregateFolderCount);
        Assert.Equal(1, root.AggregateFileCount);
        Assert.Equal(5, root.Size);
    }
    
    [Fact]
    public async Task Root_Recompute_MatchesManualPostOrderAcrossChildren()
    {
        var root = new RootNode();
        var a = new FolderNode(PathUtil.AbsPath("/", "R", "A"));
        var b = new FolderNode(PathUtil.AbsPath("/", "R", "B"));
        var c = new FolderNode(PathUtil.AbsPath("/", "R", "C"));

        a.AddFileSystemNode(new FileNode(PathUtil.AbsPath("/", "R", "A", "a.bin"), 1));
        b.AddFileSystemNode(new FileNode(PathUtil.AbsPath("/", "R", "B", "b.bin"), 2));
        c.AddFileSystemNode(new FileNode(PathUtil.AbsPath("/", "R", "C", "c.bin"), 3));

        root.AddFileSystemNode(a);
        root.AddFileSystemNode(b);
        root.AddFileSystemNode(c);

        // Manual post-order for children
        a.UpdateFolderStats();
        b.UpdateFolderStats();
        c.UpdateFolderStats();
        root.UpdateFolderStats();

        var manualFiles = root.AggregateFileCount;
        var manualFolders = root.AggregateFolderCount;
        var manualBytes = root.Size;

        // Reset aggregates
        a.AggregateFileCount = a.AggregateFolderCount = 0; a.Size = 0;
        b.AggregateFileCount = b.AggregateFolderCount = 0; b.Size = 0;
        c.AggregateFileCount = c.AggregateFolderCount = 0; c.Size = 0;
        root.AggregateFileCount = root.AggregateFolderCount = 0; root.Size = 0;

        // Recompute via helper
        await root.RecomputeSubtreeAggregatesAsync();

        Assert.Equal(manualFiles, root.AggregateFileCount);
        Assert.Equal(manualFolders, root.AggregateFolderCount);
        Assert.Equal(manualBytes, root.Size);
    }
}