using System.IO;
using DuplicateFileFinderLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateFileFinderLibTests;

[TestClass]
public class FileNodeTests
{
    private static readonly string TestFileName = "test.data";
    private TestDir _testDir = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void CreateTestFile()
    {
        _testDir = new TestDir(TestContext.FullyQualifiedTestClassName);

        _testDir.CreateTestFile(TestFileName, 244, "FileNodeTest");
    }

    [TestMethod]
    public void FileNodeTest()
    {
        var file = new FileNode(_testDir.GetFilePath(TestFileName));

        Assert.AreEqual(244, file.Size);
    }

    [TestMethod]
    public void ComputeChecksumTest()
    {
        var file = new FileNode(_testDir.GetFilePath(TestFileName));

        // full checksum
        file.ComputeChecksum().Wait();
        Assert.AreEqual("fb5293cad8167cf74069b009bd755654".ToUpper(), file.Checksum);
        Assert.IsTrue(file.FullHashCalculated);

        // partial checksum
        file.ComputeChecksum(testSize: 150).Wait();
        Assert.AreEqual("dadb817c9ab855a4d1e07e5dbaa7645a".ToUpper(), file.Checksum);
        Assert.IsFalse(file.FullHashCalculated);
    }

    [TestMethod]
    public void WritesCsvEntryTest()
    {

        string expected = $"File,\"{Path.GetFullPath(_testDir.GetFilePath(TestFileName))}\",244,,\".data\"," + "fb5293cad8167cf74069b009bd755654".ToUpper();

        var file = new FileNode(_testDir.GetFilePath(TestFileName));

        file.ComputeChecksum().Wait();

        StringWriter sw = new StringWriter();
        file.WriteCsvEntries(sw);
        sw.Close();

        Assert.IsTrue(TestUtil.CsvStringCompare(expected, sw.ToString()));
    }
}