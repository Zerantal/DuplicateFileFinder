using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using DuplicateFileFinderLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog.Filters;

namespace DuplicateFileFinderLibTests;

[TestClass]
public class FolderNodeTests
{

    public TestContext TestContext { get; set; } = null!;

    private TestDir CreateTestDir()
    {
        var testDir = new TestDir(TestContext.FullyQualifiedTestClassName);
        testDir
            .CreateTestFile("f1.txt", 0, "")
            .CreateTestFile("f2.txt", 78, "qwerty")
            .CreateTestDir("d1")
            .CreateTestFile("f3.data", 26, "poiuy")
            .CreateTestDir("d12").
            CreateTestFile("f12_1.data", 12, "asdf");

        return testDir;
    }

    [TestMethod]
    public void FolderNodeTest_null()
    {
        [ExcludeFromCodeCoverage]
        void Actual()
        {
            // ReSharper disable once ObjectCreationAsStatement
            new FolderNode(null!);
        }

        Assert.ThrowsException<ArgumentNullException>((Action)Actual);
    }

    [TestMethod]
    public void FolderNodeTest()
    {
        TestDir testDir = CreateTestDir();
        var actual = new FolderNode(testDir.DirName);

        Assert.AreEqual(1, actual.AggregateFolderCount);
        Assert.AreEqual(0, actual.Files.Count);
        Assert.AreEqual(0, actual.SubFolders.Count);
        Assert.AreEqual(TestContext.FullyQualifiedTestClassName, actual.Name);
    }

    [TestMethod]
    public void ExportCsvTest()
    {
        TestDir testDir = CreateTestDir();
        var exportFolder = new FolderNode(testDir.DirName);
        exportFolder.PopulateFolderInfo();
        exportFolder.UpdateFolderStats();
        foreach (var file in exportFolder.Files)
            file.ComputeChecksum().Wait();

        exportFolder.ComputeChecksum();
        exportFolder.UpdateFolderStats();

        StringWriter sw = new StringWriter();
        exportFolder.WriteCsvEntries(sw);
        sw.Close();

        var rootScanDir = Path.GetFullPath(testDir.DirName);
        var expectedExport = $"Folder,\"{rootScanDir}\",78,2,,\n" +
                             $"File,\"{rootScanDir}\\f1.txt\",0,,\".txt\",D41D8CD98F00B204E9800998ECF8427E\n" +
                             $"File,\"{rootScanDir}\\f2.txt\",78,,\".txt\",C627323F600D158D626F7EBC3FE2A4ED\n" +
                             $"Folder,\"{rootScanDir}\\d1\",0,0,,";

        Assert.IsTrue(TestUtil.CsvStringCompare(expectedExport, sw.ToString()));
    }

    [TestMethod]
    public void PopulateFolderInfoTest()
    {
        TestDir testDir = CreateTestDir();
        var folder = new FolderNode(testDir.DirName);

        folder.PopulateFolderInfo();

        Assert.AreEqual(2, folder.Files.Count);
        Assert.AreEqual(1, folder.SubFolders.Count);
    }

    [TestMethod]
    public void UpdateFolderStatsTest()
    {
        TestDir testDir = CreateTestDir();
        var folder = new FolderNode(testDir.DirName);

        folder.PopulateFolderInfo();
        folder.UpdateFolderStats();

        Assert.AreEqual(2, folder.AggregateFileCount);
        Assert.AreEqual(2, folder.AggregateFolderCount);
        Assert.AreEqual(78, folder.Size);
        Assert.AreEqual(2, folder.Files.Count);
        Assert.AreEqual(1, folder.SubFolders.Count);
    }

    [TestMethod]
    public void ComputeChecksumTest()
    {
        // is this test right?
        TestDir testDir = CreateTestDir();
        var folder = new FolderNode(testDir.DirName);

        folder.ComputeChecksum();

        Assert.AreEqual("D41D8CD98F00B204E9800998ECF8427E", folder.Checksum);
    }
}
