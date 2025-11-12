using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Tree;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;
// ReSharper disable InconsistentNaming

// ReSharper disable StringLiteralTypo
// ReSharper disable CommentTypo

namespace DuplicateFileFinderLibTests.Tree;

public sealed class FolderNodeTests : IDisposable
{
    private readonly TempFsFixture _fs = new();

    private static readonly string[] Expected =
    [
        "down:/root",
        "down:/root/sub1",
        "up:/root/sub1",
        "down:/root/sub2",
        "up:/root/sub2",
        "up:/root"
    ];

    public void Dispose()
    {
        _fs.Dispose();
    }

    private static string Md5UpperHex(string s)
    {
        var bytes = Convert.FromHexString(s);
        var hash = MD5.HashData(bytes);
        return string.Concat(hash.Select(b => b.ToString("X2")));
    }

    [Fact]
    public void CsvRowDataCtor_SetsExpectedProperties()
    {
        // arrange
        var row = new CsvRowData
        {
            Path = "/tmp/folder",
            Size = 12345,
            FileCount = 7,
            Checksum = "CAFEBABE",
            Group = 5
        };

        // act
        var node = new FolderNode(row);

        // assert
        Assert.Equal("/tmp/folder", node.Path);
        Assert.Equal(12345, node.Size);
        Assert.Equal(7, node.AggregateFileCount);
        Assert.Equal("CAFEBABE", node.ChecksumHex);
        Assert.Equal(5, node.Group);

        // default AggregateFolderCount starts at 1 in ctor
        Assert.Equal(1, node.AggregateFolderCount);
    }

    [Fact]
    public void UpdateFolderStats_ComputesAggregateCountsAndSizes()
    {
        // root/
        //   top.bin (50 bytes)
        //   sub1/
        //     f1.bin (100 bytes)
        //     f2.bin (200 bytes)
        //   sub2/
        //     f3.bin (500 bytes)
    
        // arrange + build tree
        var fTop = new FileNodeBuilder().Path("/root/top.bin").Size(50).Build();
        var f1 = new FileNodeBuilder().Path("/root/sub1/f1.bin").Size(100).Build();
        var f2 = new FileNodeBuilder().Path("/root/sub1/f2.bin").Size(200).Build();
        var f3 = new FileNodeBuilder().Path("/root/sub2/f3.bin").Size(500).Build();
        var sub1 = new FolderNodeBuilder("/root/sub1").File(f1).File(f2).Build();
        var sub2 = new FolderNodeBuilder("/root/sub2").File(f3).Build();
        var root = new FolderNodeBuilder("/root").File(fTop).Folder(sub1).Folder(sub2).Build();
    
        // build tree
        // act
        sub1.UpdateFolderStats();
        sub2.UpdateFolderStats();
        root.UpdateFolderStats();
    
        // assert AggregateFileCount:
        // sub1: 2 files
        Assert.Equal(2, sub1.AggregateFileCount);
        // sub2: 1 file
        Assert.Equal(1, sub2.AggregateFileCount);
        // root: 1 top file + sub1(2) + sub2(1) = 4
        Assert.Equal(4, root.AggregateFileCount);
    
        // assert Size (sum of direct children sizes only):
        // sub1 direct children: 100 + 200 = 300
        Assert.Equal(f1.Size + f2.Size, sub1.Size);
        // sub2 direct children: 500
        Assert.Equal(f3.Size, sub2.Size);
        // root direct children: 50 (top.bin) + sub1.Size(300) + sub2.Size(500) = 850
        Assert.Equal(fTop.Size + sub1.Size, sub2.Size, root.Size);
    
        // assert AggregateFolderCount:
        // sub1 has no subfolders -> 1 (including self)
        Assert.Equal(1, sub1.AggregateFolderCount);
        // sub2 has no subfolders -> 1 (including self)
        Assert.Equal(1, sub2.AggregateFolderCount);
        // root has 2 direct subfolders;
        // so: 2 + 1 (including self) = 3
        Assert.Equal(3, root.AggregateFolderCount);
    }

    [Fact]
    public void WriteCsvEntries_WritesFolder_File_AndSubfolderRecurse()
    {
        // arrange
        // root/
        //   a.txt (10 bytes)
        //   b.bin (20 bytes)
        //   sub/
        //     c.log (5 bytes)

        var modifiedTime = DateTimeOffset.Now;
        var c = new FileNodeBuilder().Path("/root/sub/c.log").Size(5).Build();
        var sub = new FolderNodeBuilder("/root/sub", DateTimeOffset.Now - TimeSpan.FromDays(1), modifiedTime)
            .File(c).Build();
        var a = new FileNodeBuilder().Path("/root/a.txt").Size(20).Build();
        var b = new FileNodeBuilder().Path("/root/b.bin").Size(10).Build();
        var root = new FolderNodeBuilder("/root", DateTimeOffset.Now - TimeSpan.FromDays(2), modifiedTime)
            .File(a).File(b).Folder(sub).Build();

        // To get sensible AggregateFileCount/Size values in CSV:
        sub.UpdateFolderStats();
        root.UpdateFolderStats();

        using var sw = new StringWriter();

        // act
        root.WriteCsvEntries(sw);

        // assert
        var csv = sw.ToString().TrimEnd().Split(Environment.NewLine);

        // Expect first line to be folder root info
        // Format: Folder,"{Path}",{Size},{AggregateFileCount},,{Checksum},{Group}
        Assert.StartsWith("Folder,\"" + root.Path + "\",", csv[0]);

        // We should see a line for a.txt:
        // File,"{Path}",{CreationTimeUtc},{Size},,{Checksum},{Group}
        var expected = string.Join(',', "File", $"\"{a.Path}\"", a.CreationTimeUtc, a.ModifiedTimeUtc, a.Size, "", "");
        Assert.Contains(csv, l => l.StartsWith(expected));

        // We should see b.bin:
        Assert.Contains(csv, l => l.StartsWith("File,\"" + b.Path + $"\",{b.CreationTimeUtc},{b.ModifiedTimeUtc},{b.Size},,"));

        // We should see the subfolder line:
        Assert.Contains(csv, l => l.StartsWith("Folder,\"" + sub.Path + "\","));

        // And c.log:
        Assert.Contains(csv, l => l.StartsWith("File,\"" + c.Path + $"\",{c.CreationTimeUtc},{c.ModifiedTimeUtc},{c.Size},,"));
    }

    [Fact]
    public void ComputeChecksum_ComposesChildChecksumsInOrder()
    {
        // arrange
        // We'll simulate:
        // root
        //   file1 (Checksum="AAAA")
        //   file2 (Checksum="BBBB")
        //   sub
        //     file3 (Checksum="CCCC")
        //
        // Expected:
        // sub.Checksum = MD5("CCCC")
        // root.Checksum = MD5("AAAABBBB" + sub.Checksum)

        // We don't actually need file contents here for checksum;
        // we'll bypass FileNode(string path) and instead use the CsvRowData ctor
        // so we can directly set Checksum without reading disk.

        var file1 = new FileNode(new CsvRowData
        {
            Path = "/tmp/file1",
            Size = 1,
            Checksum = "AAAA",
            Group = -1
        });

        var file2 = new FileNode(new CsvRowData
        {
            Path = "/tmp/file2",
            Size = 2,
            Checksum = "BBBB",
            Group = -1
        });

        var file3 = new FileNode(new CsvRowData
        {
            Path = "/tmp/file3",
            Size = 3,
            Checksum = "CCCC",
            Group = -1
        });

        var sub = new FolderNodeBuilder("/tmp/sub").File(file3).Build();
        var root = new FolderNodeBuilder("/tmp/root").File(file1).File(file2).Folder(sub).Build();

        // act
        // compute bottom-up
        sub.ComputeChecksum(); // should hash "CCCC"
        root.ComputeChecksum(); // should hash "AAAABBBB" + sub.Checksum

        // assert sub first
        var expectedSub = Md5UpperHex("CCCC");
        Assert.Equal(expectedSub, sub.ChecksumHex);

        var expectedRootConcat = "AAAA" + "BBBB" + expectedSub;
        var expectedRoot = Md5UpperHex(expectedRootConcat);
        Assert.Equal(expectedRoot, root.ChecksumHex);
    }

    [Fact]
    public void ComputeChecksum_StopsIfAnyFileChecksumMissing()
    {
        // arrange
        var badFile = new FileNode(new CsvRowData
        {
            Path = "/tmp/bad",
            Size = 10,
            Group = -1
        });
    
        var folder = new FolderNodeBuilder("/tmp/folder").File(badFile).Build();
    
        // act
        folder.ComputeChecksum();
    
        // assert
        Assert.Empty(folder.ChecksumHex);
    }

    [Fact]
    public void ComputeChecksum_StopsIfAnySubfolderChecksumMissing()
    {
        // arrange
        var leafFile = new FileNode(new CsvRowData
        {
            Path = "/tmp/child/leaf",
            Group = -1
            // Don't provide checksum, so child folder can't compute checksum
        });
    
        var childFolder = new FolderNodeBuilder("/tmp/child").File(leafFile).Build();
    
        var parent = new FolderNodeBuilder("/tmp").Folder(childFolder).Build();
    
        // act
        parent.ComputeChecksum();
    
        // assert
        Assert.Empty(parent.ChecksumHex);
    }

    [Fact]
    public async Task TraverseFolders_DepthFirst_DownThenUp()
    {
        // arrange
        //
        // root
        //  ├─ sub1
        //  └─ sub2
        //
        var sub1 = new FolderNodeBuilder("/root/sub1").Build();
        var sub2 = new FolderNodeBuilder("/root/sub2").Build();
        var root = new FolderNodeBuilder("/root").Folder(sub1).Folder(sub2).Build();
    
        var order = new System.Collections.Generic.List<string>();
    
        Task Down(FolderNode n)
        {
            order.Add("down:" + n.Path);
            return Task.CompletedTask;
        }
    
        Task Up(FolderNode n)
        {
            order.Add("up:" + n.Path);
            return Task.CompletedTask;
        }
    
        // act
        await root.TraverseFolders(Down, Up);
    
        // assert
        Assert.Equal(
            Expected,
            order
        );
    }

    [Fact]
    public void FolderChecksum_IsIndependentOfOrderAndNames()
    {
        // Arrange
        // Build three mock FileNodes with fixed checksums.
        var h1 = MD5.HashData("A"u8.ToArray());
        var h2 = MD5.HashData("B"u8.ToArray());
        var h3 = MD5.HashData("C"u8.ToArray());
        var t0 = DateTimeOffset.UnixEpoch;

        // Folder 1: children in order A, B, C
        var A = new FolderNodeBuilder("/X/A", t0)
            .File(new FileNodeBuilder().Path("/X/A/1").Created(t0).Checksum(h1).Build())
            .File(new FileNodeBuilder().Path("/X/A/2").Created(t0).Checksum(h2).Build())
            .File(new FileNodeBuilder().Path("/X/A/3").Created(t0).Checksum(h3).Build())
            .Build();
        A.ComputeChecksum();
    
        // Folder 2: same hashes but reversed order and different names
        var B = new FolderNodeBuilder("/X/B", t0)
            .File(new FileNodeBuilder().Path("/X/B/p").Created(t0).Checksum(h3).Build())
            .File(new FileNodeBuilder().Path("/X/B/q").Created(t0).Checksum(h1).Build())
            .File(new FileNodeBuilder().Path("/X/B/r").Created(t0).Checksum(h2).Build())
            .Build();
        B.ComputeChecksum();
        
        // Folder 3: same as others but one checksum different
        var h1Alt = MD5.HashData("A_ALT"u8.ToArray());
        var C = new FolderNodeBuilder("/X/B", t0)
            .File(new FileNodeBuilder().Path("/X/B/p").Created(t0).Checksum(h3).Build())
            .File(new FileNodeBuilder().Path("/X/B/q").Created(t0).Checksum(h1Alt).Build())
            .File(new FileNodeBuilder().Path("/X/B/r").Created(t0).Checksum(h2).Build())
            .Build();
        C.ComputeChecksum();
        
        Assert.NotNull(A.ChecksumBytes);
        Assert.NotNull(B.ChecksumBytes);
        Assert.NotNull(C.ChecksumBytes);
        
        // Folders 1 and 2 should have identical folder checksums,
        // because their sets of child hashes are equal despite different order/names.
        Assert.Equal(Convert.ToHexString(A.ChecksumBytes!), Convert.ToHexString(B.ChecksumBytes!));
    
        // Folder 3 differs by one child hash, so its folder checksum should differ.
        Assert.NotEqual(Convert.ToHexString(A.ChecksumBytes!), Convert.ToHexString(C.ChecksumBytes!));
    }
    
    [Fact]
    public void FolderChecksum_SkipsWhenAnyChildChecksumMissing()
    {
        var f1 = new FileNode("/tmp/a", 0) { ChecksumBytes = MD5.HashData("A"u8.ToArray()) };
        var f2 = new FileNode("/tmp/b", 0) { ChecksumBytes = null }; // missing checksum
    
        var folder = new FolderNode("/tmp/root");
        folder.AddFileSystemNode(f1);
        folder.AddFileSystemNode(f2);
    
        folder.ComputeChecksum();
    
        Assert.Null(folder.ChecksumBytes);
    }

    [Fact]
    public async Task DeepCloneSubtree_CopiesShapeAndMetadata()
    {
        var rootCreationTime = DateTimeOffset.Now - TimeSpan.FromDays(5);
        var aCreateTime = DateTimeOffset.Now - TimeSpan.FromDays(4);
        var bCreateTime = DateTimeOffset.Now - TimeSpan.FromDays(3);
        var f1CreateTime = DateTimeOffset.Now - TimeSpan.FromDays(2);
        var f2CreateTime = DateTimeOffset.Now - TimeSpan.FromDays(1);
        
        var f2 = new FileNodeBuilder().Path("/tmp/root/B/y.bin").Size(456).Created(f2CreateTime).Group(8).Build();
        var f1 = new FileNodeBuilder().Path("/tmp/root/A/x.txt").Size(123).Created(f1CreateTime).Group(7).Build();
        var b = new FolderNodeBuilder("/tmp/root/B", bCreateTime).File(f2).Build();
        var a = new FolderNodeBuilder("/tmp/root/A", aCreateTime).File(f1).Build();
        var root = new FolderNodeBuilder("/tmp/root", rootCreationTime).Folder(a).Folder(b).Build();
        
        await root.RecomputeSubtreeAggregatesAsync();

        var clone = root.DeepCloneSubtree();

        // Different instances
        Assert.NotSame(root, clone);
        Assert.NotSame(a, clone.SubFolders.Single(sf => sf.Path.EndsWith("/A")));
        Assert.NotSame(f1, clone.SubFolders.Single(sf => sf.Path.EndsWith("/A")).Files.Single());

        // Same structure and key metadata
        Assert.Equal(root.AggregateFileCount, clone.AggregateFileCount);
        Assert.Equal(root.AggregateFolderCount, clone.AggregateFolderCount);
        Assert.Equal(f1.Group, clone.SubFolders.Single(sf => sf.Path.EndsWith("/A")).Files.Single().Group);
    }

    [Fact]
    public void FolderNode_IncludesSelf_NoChildren()
    {
        var a = new FolderNode(PathUtil.P("/", "a")); // no children
    
        a.UpdateFolderStats();
    
        Assert.Equal(0, a.AggregateFileCount);
        Assert.Equal(1, a.AggregateFolderCount); // includes self
        Assert.Equal(0, a.Size);
    }
    
    [Fact]
    public void FolderNode_IncludesSelf_WithChildFolderAndFiles()
    {
        var a = new FolderNode(PathUtil.P("/", "a"));
        var a1 = new FolderNode(PathUtil.P("/", "a", "a1"));
        var f1 = new FileNode(PathUtil.P("/", "a", "f1.bin"), 10);
        var f2 = new FileNode(PathUtil.P("/", "a", "a1", "f2.bin"), 20);
    
        a1.AddFileSystemNode(f2);
        a.AddFileSystemNode(f1);
        a.AddFileSystemNode(a1);
    
        // Post-order recompute (children first)
        a1.UpdateFolderStats();
        a.UpdateFolderStats();
    
        // a1 counts: itself + its descendants
        Assert.Equal(1, a1.AggregateFolderCount); // a1
        Assert.Equal(1, a1.AggregateFileCount); // f2
        Assert.Equal(20, a1.Size);
    
        // a counts: itself + a1
        Assert.Equal(2, a.AggregateFolderCount); // a + a1
        Assert.Equal(2, a.AggregateFileCount); // f1 + f2
        Assert.Equal(30, a.Size); // 10 + 20
    }    
    
    [Fact]
    public void FolderChecksum_Ignores_Names_And_Order()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var h1 = MD5.HashData("A"u8.ToArray());
        var h2 = MD5.HashData("B"u8.ToArray());
        var h3 = MD5.HashData("C"u8.ToArray());

        var fA = new FolderNodeBuilder("/X/A", t0)
            .File(new FileNodeBuilder().Path("/X/A/1").Created(t0).Checksum(h1).Build())
            .File(new FileNodeBuilder().Path("/X/A/2").Created(t0).Checksum(h2).Build())
            .File(new FileNodeBuilder().Path("/X/A/3").Created(t0).Checksum(h3).Build())
            .Build();
        fA.ComputeChecksum();

        var fB = new FolderNodeBuilder("/X/B", t0)
            .File(new FileNodeBuilder().Path("/X/B/p").Created(t0).Checksum(h3).Build())
            .File(new FileNodeBuilder().Path("/X/B/q").Created(t0).Checksum(h1).Build())
            .File(new FileNodeBuilder().Path("/X/B/r").Created(t0).Checksum(h2).Build())
            .Build();
        fB.ComputeChecksum();

        Assert.NotNull(fA.ChecksumBytes);
        Assert.NotNull(fB.ChecksumBytes);
        Assert.Equal(Convert.ToHexString(fA.ChecksumBytes!), Convert.ToHexString(fB.ChecksumBytes!));
    }
}