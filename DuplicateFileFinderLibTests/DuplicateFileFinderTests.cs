using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using DuplicateFileFinderLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// ReSharper disable StringLiteralTypo

namespace DuplicateFileFinderLibTests;

[TestClass]
public class DuplicateFileFinderTests
{
    public TestContext TestContext { get; set; } = null!;

    private readonly TestDir _testScanRoot = new("scanTestRoot");

    private TestDir CreateScanLocationTestDir(string rootDir = "scanTestDir")
    {
        var testDir = _testScanRoot.CreateTestDir(rootDir);
        testDir.CreateTestFile("f1.txt", 80, "qwerty")
            .CreateTestFile("f2.txt", 128, "uiop[]")
            .CreateTestDir("d1")
                .CreateTestFile("f11.txt", 50, "asdfg")
                .CreateTestFile("f12.txt", 105, "asdfg")
                .CreateTestFile("f13.txt", 80, "qwerty");

        return testDir;
    }

    private TestDir CreateScanLocationTestDir2()
    {
        var testDir = _testScanRoot.CreateTestDir("scanTestDir2");
        testDir.CreateTestFile("f1.txt", 80, "qwerty")
            .CreateTestFile("f2.txt", 80, "qwerty")
            .CreateTestDir("d1")
            .CreateTestFile("d1f1.txt", 150, "asdfg")
            .CreateTestFile("d1f2.txt", 105, "asdfg")
            .CreateTestFile("d1f3.txt", 80, "qwerty")
            .CreateTestDir("d2")
            .CreateTestFile("d2f1.txt", 80, "qwerty");

        return testDir;
    }


    [TestMethod]
    public async Task ScanLocationTest_EmptyDir()
    {
        TestDir emptyDir = new TestDir("empty_dir");

        DuplicateFileFinder dupFileFinder = new();

        await dupFileFinder.ScanLocation(emptyDir.DirName);

        Assert.AreEqual(0, dupFileFinder.DuplicateFileCount);
        Assert.AreEqual(1, dupFileFinder.LocationCount);
        Assert.AreEqual(0, dupFileFinder.SpaceTakenByDuplicates);
    }

    [TestMethod]
    public async Task ScanLocationTest()
    {
        var testDir = CreateScanLocationTestDir();
        DuplicateFileFinder dupFileFinder = new()
        {
            ChecksumTestSize = 40
        };
        await dupFileFinder.ScanLocation(testDir.DirName);

        Assert.AreEqual(1, dupFileFinder.DuplicateFileCount);
        Assert.AreEqual(1, dupFileFinder.LocationCount);
        Assert.AreEqual(80, dupFileFinder.SpaceTakenByDuplicates);

        var testDir2 = CreateScanLocationTestDir2();
        await dupFileFinder.ScanLocation(testDir2.DirName);

        Assert.AreEqual(6, dupFileFinder.DuplicateFileCount);
        Assert.AreEqual(2, dupFileFinder.LocationCount);
        Assert.AreEqual(505, dupFileFinder.SpaceTakenByDuplicates);

        // scan testDir2 again. Should be no change
        await dupFileFinder.ScanLocation(testDir2.DirName);
        Assert.AreEqual(6, dupFileFinder.DuplicateFileCount);
        Assert.AreEqual(2, dupFileFinder.LocationCount);
        Assert.AreEqual(505, dupFileFinder.SpaceTakenByDuplicates);

        TestDir testDir3 = _testScanRoot.CreateTestDir("e_dir").CreateTestFile("d3f1", 134, "poawsfgasdklg");
        await dupFileFinder.ScanLocation(testDir3.DirName);
        FolderNode tmpNode = dupFileFinder.root;
        while (tmpNode.Name != "scanTestRoot")
        {
            Assert.AreEqual(1, tmpNode.SubFoldersContainingDuplicates.Count);
            tmpNode = tmpNode.SubFoldersContainingDuplicates[0];
        }
        Assert.AreEqual(2, tmpNode.SubFoldersContainingDuplicates.Count);

    }

    [TestMethod]
    public void ScanLocationDuplicateFolderTest()
    {
        string dupFolderRoot = "dupFolderTest";
        var testDir = _testScanRoot.CreateTestDir(dupFolderRoot);
        CreateScanLocationTestDir(dupFolderRoot + "/dir1");
        CreateScanLocationTestDir(dupFolderRoot + "/dir2");
        DuplicateFileFinder dupFileFinder = new()
        {
            ChecksumTestSize = 40
        };
        dupFileFinder.ScanLocation(testDir.DirName).Wait();

        Assert.AreEqual(6, dupFileFinder.DuplicateFileCount);
        Assert.AreEqual(1, dupFileFinder.LocationCount);
        Assert.AreEqual(523, dupFileFinder.SpaceTakenByDuplicates);
    }

    [TestMethod]
    public void CsvExportImportTest()
    {
        DuplicateFileFinder exporter = new();
        DuplicateFileFinder importer = new();

        var testDir = CreateScanLocationTestDir();
        var testDir2 = CreateScanLocationTestDir2();
        exporter.ScanLocation(testDir.DirName).Wait();
        exporter.ScanLocation(testDir2.DirName).Wait();
        StringWriter sw = new StringWriter();
        exporter.ExportToCsv(sw);

        var rootScanDir = Path.GetFullPath(_testScanRoot.DirName);
        var expectedExport = "File/Folder,Path,Size,File Count,Extension,MD5\r\n" +
                             $"ScanRootFolder,\"{rootScanDir}\\scanTestDir\\\",443,5,,\r\n" +
                             $"File,\"{rootScanDir}\\scanTestDir\\f1.txt\",80,,\".txt\",5CCCA7B91B533378DC0B13236F539A7D\n" +
                             $"File,\"{rootScanDir}\\scanTestDir\\f2.txt\",128,,\".txt\",\n" +
                             $"Folder,\"{rootScanDir}\\scanTestDir\\d1\",235,3,,\n" +
                             $"File,\"{rootScanDir}\\scanTestDir\\d1\\f11.txt\",50,,\".txt\",\n" +
                             $"File,\"{rootScanDir}\\scanTestDir\\d1\\f12.txt\",105,,\".txt\",5AFEF87DB74F218999589CE9FD40BD34\n" +
                             $"File,\"{rootScanDir}\\scanTestDir\\d1\\f13.txt\",80,,\".txt\",5CCCA7B91B533378DC0B13236F539A7D\n" +
                             $"ScanRootFolder,\"{rootScanDir}\\scanTestDir2\\\",575,6,,\n" +
                             $"File,\"{rootScanDir}\\scanTestDir2\\f1.txt\",80,,\".txt\",5CCCA7B91B533378DC0B13236F539A7D\n" +
                             $"File,\"{rootScanDir}\\scanTestDir2\\f2.txt\",80,,\".txt\",5CCCA7B91B533378DC0B13236F539A7D\n" +
                             $"Folder,\"{rootScanDir}\\scanTestDir2\\d1\",415,4,,\n" +
                             $"File,\"{rootScanDir}\\scanTestDir2\\d1\\d1f1.txt\",150,,\".txt\",\n" +
                             $"File,\"{rootScanDir}\\scanTestDir2\\d1\\d1f2.txt\",105,,\".txt\",5AFEF87DB74F218999589CE9FD40BD34\n" +
                             $"File,\"{rootScanDir}\\scanTestDir2\\d1\\d1f3.txt\",80,,\".txt\",5CCCA7B91B533378DC0B13236F539A7D\n" +
                             $"Folder,\"{rootScanDir}\\scanTestDir2\\d1\\d2\",80,1,,6FCE1FD5C2FEA5C45DBE56D687CF7CC5\n" +
                             $"File,\"{rootScanDir}\\scanTestDir2\\d1\\d2\\d2f1.txt\",80,,\".txt\",5CCCA7B91B533378DC0B13236F539A7D\n";


        Assert.IsTrue(TestUtil.CsvStringCompare(expectedExport, sw.ToString()));

        StringReader sr = new StringReader(sw.ToString());
        importer.ImportFromCsv(sr);
        Assert.AreEqual(6, importer.DuplicateFileCount);
        Assert.AreEqual(2, importer.LocationCount);
        Assert.AreEqual(505, importer.SpaceTakenByDuplicates);
    }

    [TestMethod]
    [SuppressMessage("ReSharper", "AccessToModifiedClosure")]
    public void ImportFromCsvErrorTest()
    {
        DuplicateFileFinder dupFileFinder = new();

        // empty file
        var emptyStr = "";
        StringReader sr = new StringReader(emptyStr);
        sr.ReadToEnd();
        var ex = Assert.ThrowsException<InvalidFormatException>(() => dupFileFinder.ImportFromCsv(sr));
        Assert.AreEqual("Empty file", ex.Message);

        // wrong number of heading columns
        var badData = "File/Folder,Path,Size,File Count,Extension\r\n";
        sr = new StringReader(badData);
        ex = Assert.ThrowsException<InvalidFormatException>(() => dupFileFinder.ImportFromCsv(sr));
        Assert.AreEqual("Insufficient number of headings detected", ex.Message);

        // Successful import
        var correctData = "File/Folder,Path,Size,File Count,Extension,MD5\r\n" +
                             "File,\"\\scanTestDir\\f1.txt\",80,,\".txt\",5CCCA7B91B533378DC0B13236F539A7D\n";
        sr = new StringReader(correctData);

        // Incorrect number of fields on data row
        badData = "File/Folder,Path,Size,File Count,Extension,MD5\r\n" +
                          "\"\\scanTestDir\\f1.txt\",80,,\".txt\",5CCCA7B91B533378DC0B13236F539A7D\n";
        sr = new StringReader(badData);
        ex = Assert.ThrowsException<InvalidFormatException>(() => dupFileFinder.ImportFromCsv(sr));
        Assert.AreEqual("Error parsing data on row 2", ex.Message);

        // Invalid item in col 1
        badData = "File/Folder,Path,Size,File Count,Extension,MD5\r\n" +
                  "Gile,\"\\scanTestDir\\f1.txt\",80,,\".txt\",5CCCA7B91B533378DC0B13236F539A7D\n";
        sr = new StringReader(badData);
        ex = Assert.ThrowsException<InvalidFormatException>(() => dupFileFinder.ImportFromCsv(sr));
        Assert.AreEqual("Error parsing data on row 2", ex.Message);

        // Invalid item in col 3
        badData = "File/Folder,Path,Size,File Count,Extension,MD5\r\n" +
                  "File,\"\\scanTestDir\\f1.txt\",8f,,\".txt\",5CCCA7B91B533378DC0B13236F539A7D\n";
        sr = new StringReader(badData);
        ex = Assert.ThrowsException<InvalidFormatException>(() => dupFileFinder.ImportFromCsv(sr));
        Assert.AreEqual("Error parsing data on row 2", ex.Message);

        // Test that Folder entry must contain a File Count value
        badData = "File/Folder,Path,Size,File Count,Extension,MD5\r\n" +
                  "Folder,\"\\scanTestDir\\f1.txt\",80,,\".txt\",5CCCA7B91B533378DC0B13236F539A7D\n";
        sr = new StringReader(badData);
        ex = Assert.ThrowsException<InvalidFormatException>(() => dupFileFinder.ImportFromCsv(sr));
        Assert.AreEqual("Error parsing data on row 2", ex.Message);
    }
}
