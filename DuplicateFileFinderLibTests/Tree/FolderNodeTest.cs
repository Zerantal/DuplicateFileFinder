using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Tree;using DuplicateFileFinderLibTests.TestUtils;
using Xunit;

// ReSharper disable StringLiteralTypo
// ReSharper disable CommentTypo

namespace DuplicateFileFinderLibTests.Tree;

public sealed class FolderNodeTests : IDisposable
{
    // We'll create a temp directory per test class instance to hold any files
    // needed for FileNode construction. Clean it up in Dispose().
    private readonly string _tempRoot;

    private static readonly string[] Expected =
    [
        "down:/root",
        "down:/root/sub1",
        "up:/root/sub1",
        "down:/root/sub2",
        "up:/root/sub2",
        "up:/root"
    ];

    public FolderNodeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "FolderNodeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup; tests shouldn't fail because cleanup failed
        }
    }

    private string CreateTempFile(string name, byte[] content)
    {
        var fullPath = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
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

        // arrange
        var fTopPath = CreateTempFile("top.bin", new byte[50]);
        var f1Path = CreateTempFile(Path.Combine("sub1", "f1.bin"), new byte[100]);
        var f2Path = CreateTempFile(Path.Combine("sub1", "f2.bin"), new byte[200]);
        var f3Path = CreateTempFile(Path.Combine("sub2", "f3.bin"), new byte[500]);

        var root = new FolderNode(_tempRoot);
        var sub1 = new FolderNode(Path.Combine(_tempRoot, "sub1"));
        var sub2 = new FolderNode(Path.Combine(_tempRoot, "sub2"));

        // build tree
        sub1.AddFileSystemNode(new FileNode(f1Path, 100));
        sub1.AddFileSystemNode(new FileNode(f2Path, 200));

        sub2.AddFileSystemNode(new FileNode(f3Path, 500));

        root.AddFileSystemNode(new FileNode(fTopPath, 50));
        root.AddFileSystemNode(sub1);
        root.AddFileSystemNode(sub2);

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
        Assert.Equal(300, sub1.Size);
        // sub2 direct children: 500
        Assert.Equal(500, sub2.Size);
        // root direct children: 50 (top.bin) + sub1.Size(300) + sub2.Size(500) = 850
        Assert.Equal(850, root.Size);

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

        var aPath = CreateTempFile("a.txt", new byte[10]);
        var bPath = CreateTempFile("b.bin", new byte[20]);
        var subDir = Path.Combine(_tempRoot, "sub");
        Directory.CreateDirectory(subDir);
        var cPath = CreateTempFile(Path.Combine("sub", "c.log"), new byte[5]);

        var root = new FolderNode(_tempRoot);
        var sub = new FolderNode(subDir);

        sub.AddFileSystemNode(new FileNode(cPath, 5));

        root.AddFileSystemNode(new FileNode(aPath, 10));
        root.AddFileSystemNode(new FileNode(bPath, 20));
        root.AddFileSystemNode(sub);

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
        // File,"{Path}",{Size},,"{Extension}",{Checksum}, {Group}
        Assert.Contains(csv, l => l.StartsWith("File,\"" + aPath + "\",10,,"));

        // We should see b.bin:
        Assert.Contains(csv, l => l.StartsWith("File,\"" + bPath + "\",20,,"));

        // We should see the subfolder line:
        Assert.Contains(csv, l => l.StartsWith("Folder,\"" + subDir + "\","));

        // And c.log:
        Assert.Contains(csv, l => l.StartsWith("File,\"" + cPath + "\",5,,"));
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

        var sub = new FolderNode("/tmp/sub");
        sub.AddFileSystemNode(file3);

        var root = new FolderNode("/tmp/root");
        root.AddFileSystemNode(file1);
        root.AddFileSystemNode(file2);
        root.AddFileSystemNode(sub);

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

        var folder = new FolderNode("/tmp/folder");
        folder.AddFileSystemNode(badFile);

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

        var childFolder = new FolderNode("/tmp/child");
        childFolder.AddFileSystemNode(leafFile);

        var parent = new FolderNode("/tmp/parent");
        parent.AddFileSystemNode(childFolder);

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
        var root = new FolderNode("/root");
        var sub1 = new FolderNode("/root/sub1");
        var sub2 = new FolderNode("/root/sub2");

        root.AddFileSystemNode(sub1);
        root.AddFileSystemNode(sub2);

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

        var file1 = new FileNode("/tmp/a.txt", 0) { ChecksumBytes = h1 };
        var file2 = new FileNode("/tmp/b.txt", 0) { ChecksumBytes = h2 };
        var file3 = new FileNode("/tmp/c.txt", 0) { ChecksumBytes = h3 };

        // Folder 1: children in order A, B, C
        var fA = new FolderNode("/tmp/folder1");
        fA.AddFileSystemNode(file1);
        fA.AddFileSystemNode(file2);
        fA.AddFileSystemNode(file3);
        fA.ComputeChecksum();

        // Folder 2: same hashes but reversed order and different names
        var g1 = new FileNode("/tmp/z.txt", 0) { ChecksumBytes = h3 };
        var g2 = new FileNode("/tmp/y.txt", 0) { ChecksumBytes = h1 };
        var g3 = new FileNode("/tmp/x.txt", 0) { ChecksumBytes = h2 };

        var fB = new FolderNode("/tmp/folder2");
        fB.AddFileSystemNode(g1);
        fB.AddFileSystemNode(g2);
        fB.AddFileSystemNode(g3);
        fB.ComputeChecksum();

        // Folder 3: same as others but one checksum different
        var h1Alt = MD5.HashData("A_ALT"u8.ToArray());
        var hFileAlt = new FileNode("/tmp/d.txt", 0) { ChecksumBytes = h1Alt };
        var fC = new FolderNode("/tmp/folder3");
        fC.AddFileSystemNode(hFileAlt);
        fC.AddFileSystemNode(file2);
        fC.AddFileSystemNode(file3);
        fC.ComputeChecksum();

        // Act / Assert
        Assert.NotNull(fA.ChecksumBytes);
        Assert.NotNull(fB.ChecksumBytes);
        Assert.NotNull(fC.ChecksumBytes);

        // Folders 1 and 2 should have identical folder checksums,
        // because their sets of child hashes are equal despite different order/names.
        Assert.Equal(Convert.ToHexString(fA.ChecksumBytes!), Convert.ToHexString(fB.ChecksumBytes!));

        // Folder 3 differs by one child hash, so its folder checksum should differ.
        Assert.NotEqual(Convert.ToHexString(fA.ChecksumBytes!), Convert.ToHexString(fC.ChecksumBytes!));
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
        var root = new FolderNode("/tmp/root");
        var a = new FolderNode("/tmp/root/A");
        var b = new FolderNode("/tmp/root/B");
        var f1 = new FileNode("/tmp/root/A/x.txt", 123) { Group = 7 };
        var f2 = new FileNode("/tmp/root/B/y.bin", 456) { Group = 8 };

        a.AddFileSystemNode(f1);
        b.AddFileSystemNode(f2);
        root.AddFileSystemNode(a);
        root.AddFileSystemNode(b);
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
        var a = new FolderNode(PathUtil.AbsPath("/", "a")); // no children

        a.UpdateFolderStats();

        Assert.Equal(0, a.AggregateFileCount);
        Assert.Equal(1, a.AggregateFolderCount); // includes self
        Assert.Equal(0, a.Size);
    }

    [Fact]
    public void FolderNode_IncludesSelf_WithChildFolderAndFiles()
    {
        var a = new FolderNode(PathUtil.AbsPath("/", "a"));
        var a1 = new FolderNode(PathUtil.AbsPath("/", "a", "a1"));
        var f1 = new FileNode(PathUtil.AbsPath("/", "a", "f1.bin"), 10);
        var f2 = new FileNode(PathUtil.AbsPath("/", "a", "a1", "f2.bin"), 20);

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
}