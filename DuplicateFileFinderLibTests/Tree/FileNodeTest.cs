using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Tree;
using Xunit;

namespace DuplicateFileFinderLibTests.Tree;

public sealed class FileNodeTests : IDisposable
{
    private readonly string _tempRoot;

    public FileNodeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "FileNodeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private string CreateTempFile(string relativeName, byte[] content)
    {
        var fullPath = Path.Combine(_tempRoot, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    private static string Md5UpperHex(byte[] bytes)
    {
        var hash = MD5.HashData(bytes);
        return string.Concat(hash.Select(b => b.ToString("X2")));
    }

    [Fact]
    public void Ctor_FromPath_Sets_Path_Size_Group_Defaults()
    {
        // arrange
        var data = new byte[123];
        var filePath = CreateTempFile("sample.bin", data);

        // act
        var node = new FileNode(filePath, 123);

        // assert
        Assert.Equal(filePath, node.Path);
        Assert.Equal(123, node.Size);
        Assert.Equal(-1, node.Group);
        Assert.True(string.IsNullOrEmpty(node.ChecksumHex));
    }

    [Fact]
    public void Ctor_FromCsvRowData_Sets_AllProperties()
    {
        // arrange
        var row = new CsvRowData
        {
            Path = "/does/not/matter.txt",
            Size = 9999,
            Checksum = "ABCDEF1234",
            Group = 42
        };

        // act
        var node = new FileNode(row);

        // assert
        Assert.Equal("/does/not/matter.txt", node.Path);
        Assert.Equal(9999, node.Size);
        Assert.Equal("ABCDEF1234", node.ChecksumHex);
        Assert.Equal(42, node.Group);
    }

    [Fact]
    public async Task ComputeChecksum_ReadsFileAndSetsChecksum_ToUpperMd5()
    {
        // arrange
        // Pick deterministic content so we can assert the MD5.
        var content = "Hello world\n"u8.ToArray();
        var filePath = CreateTempFile("hello.txt", content);

        var node = new FileNode(filePath, content.Length);

        // act
        await node.ComputeChecksum();

        // assert
        var expected = Md5UpperHex(content);
        Assert.Equal(expected, node.ChecksumHex);
    }

    [Fact]
    public async Task ComputeChecksum_DoesNotThrow_WhenFileMissing_AndLeavesChecksumEmpty()
    {
        // arrange
        var missingPath = Path.Combine(_tempRoot, "idontexist.dat");
        var node = new FileNode(new CsvRowData
        {
            Path = missingPath,
            Size = 1234,
            Checksum = string.Empty,
            Group = -1
        });

        // act
        // This should hit the catch in ComputeChecksum()
        await node.ComputeChecksum();

        // assert
        Assert.Equal(string.Empty, node.ChecksumHex);
    }

    public class TestFileNode : FileNode
    {
        public TestFileNode(string path, long size) : base(path, size)
        {
        }

        internal TestFileNode(CsvRowData rowInfo) : base(rowInfo)
        {
        }

        public void SetChecksum(String checksum)
        {
            ChecksumHex = checksum;
        }
    };
        
    [Fact]
    public void WriteCsvEntries_WritesExpectedCsvFormat()
    {            
        // arrange
        var content = new byte[10]; // 10 bytes
        var filePath = CreateTempFile("data.bin", content);

        var node = new TestFileNode(filePath, 10);

        // give it a checksum and custom group so we can assert them
        node.SetChecksum("FEEDFACE");
        // ctor from path sets Group = -1, so let's ensure we see that exact value
        // If you later change Group before writing, update this assertion accordingly.

        using var sw = new StringWriter();

        // act
        node.WriteCsvEntries(sw);

        // assert
        var csv = sw.ToString().TrimEnd();

        // Expected format:
        // File,"{Path}",{Size},,"{Extension}",{Checksum}, {Group}
        //
        // Extension should be ".bin"
        var expectedStart = $"File,\"{filePath}\",10,,FEEDFACE,-1";
        Assert.Equal(expectedStart, csv);
    }

    [Fact]
    public void AddFileSystemNode_Throws_InvalidOperationException()
    {
        // arrange
        var filePath = CreateTempFile("leaf.txt", new byte[1]);
        var node = new FileNode(filePath, 1);

        // We'll just try to add itself. Any FileSystemNode triggers the same path.
        // We only care about the message.
        var ex = Assert.Throws<InvalidOperationException>(() => node.AddFileSystemNode(node));

        Assert.Equal("Can't add node to FileNode object", ex.Message);
    }

    [Fact]
    public void Extension_ReturnsFileExtension()
    {
        // arrange
        var filePath = CreateTempFile("document.test.ext", new byte[3]);
        var node = new FileNode(filePath, 3);

        // act
        var ext = node.Extension;

        // assert
        Assert.Equal(".ext", ext);
    }
}