using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Tree;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;
// ReSharper disable StringLiteralTypo

namespace DuplicateFileFinderLibTests.Tree;

public sealed class FileNodeTests : IDisposable
{
    private TempFsFixture _fs = new();

    public void Dispose()
    {
        _fs.Dispose();
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
        var createTime = DateTime.Now;
        var filePath = _fs.File("sample.bin", data);
        
        // act
        var node = new FileNode(filePath, 123, createTime);

        // assert
        Assert.Equal(filePath, node.Path);
        Assert.Equal(123, node.Size);
        Assert.Equal(-1, node.Group);
        Assert.Equal(createTime, node.CreationTimeUtc);
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
        var filePath = _fs.File("hello.txt", content);

        var node = new FileNode(filePath, content.Length, DateTimeOffset.Now);

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
        var missingPath = PathUtil.P(_fs.Root, "idontexist.dat");
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

    private class TestFileNode(string path, long size, DateTimeOffset creationTimeUtc) : FileNode(path, size, creationTimeUtc)
    {
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
        var filePath = _fs.File("data.bin", content);
        var creationTime = DateTimeOffset.Now;

        var node = new TestFileNode(filePath, content.Length, creationTime);

        // give it a checksum and custom group so we can assert them
        node.SetChecksum("FEEDFACE");
        // ctor from path sets Group = -1, so let's ensure we see that exact value

        using var sw = new StringWriter();

        // act
        node.WriteCsvEntries(sw);

        // assert
        var csv =  sw.ToString().TrimEnd();
        
        Assert.Matches(new Regex($"File,\"{filePath}\",.*,10,,FEEDFACE,-1"), csv);
        
    }

    [Fact]
    public void AddFileSystemNode_Throws_InvalidOperationException()
    {
        // arrange
        var filePath = _fs.File("leaf.txt", new byte[1]);
        var node = new FileNode(filePath, 1, DateTimeOffset.Now);

        // We'll just try to add itself. Any FileSystemNode triggers the same path.
        // We only care about the message.
        var ex = Assert.Throws<InvalidOperationException>(() => node.AddFileSystemNode(node));

        Assert.Equal("Can't add node to FileNode object", ex.Message);
    }

    [Fact]
    public void Extension_ReturnsFileExtension()
    {
        // arrange
        var filePath = _fs.File("document.test.ext", new byte[3]);
        var node = new FileNode(filePath, 3, DateTimeOffset.Now);

        // act
        var ext = node.Extension;

        // assert
        Assert.Equal(".ext", ext);
    }
}