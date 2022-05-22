using System;
using System.IO;
using DuplicateFileFinderLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateFileFinderLibTests;

[TestClass]
public class FolderNodeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void FolderNodeTest_null()
    {
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
        var actual = new FolderNode(TestData.Path + "TestDir1");

        Assert.AreEqual(1, actual.AggregateFolderCount);
        Assert.AreEqual(0, actual.Files.Count);
        Assert.AreEqual(0, actual.SubFolders.Count);
    }

    [TestMethod]
    public void WriteCsvEntriesTest()
    {
        var folder = new FolderNode(TestData.Path + "TestDir1");
        folder.PopulateFolderInfo();
        folder.UpdateFolderStats();
        foreach (var file in folder.Files)
            file.ComputeChecksum().Wait();

        folder.ComputeChecksum();
        folder.UpdateFolderStats();

        var expectedFile = TestData.Path + "Folder_" + TestContext.TestName + "_expected.csv";
        var actualFile = TestData.Path + "Folder_" + TestContext.TestName + "_actual.csv";

        StreamWriter sw = new StreamWriter(actualFile);
        folder.WriteCsvEntries(sw);
        sw.Close();

        Assert.IsTrue(TestData.CsvFileCompare(expectedFile, actualFile));
    }

    [TestMethod]
    public void PopulateFolderInfoTest()
    {
        var folder = new FolderNode(TestData.Path + "TestDir1");

        folder.PopulateFolderInfo();

        Assert.AreEqual(2, folder.Files.Count);
        Assert.AreEqual(1,folder.SubFolders.Count);
    }

    [TestMethod]
    public void UpdateFolderStatsTest()
    {
        var folder = new FolderNode(TestData.Path + "TestDir1");

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
        var folder = new FolderNode(TestData.Path + "TestDir1");

        folder.ComputeChecksum();

        Assert.AreEqual("D41D8CD98F00B204E9800998ECF8427E", folder.Checksum);
    }
}