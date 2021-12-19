using System;
using System.IO;
using System.Linq;
using DuplicateFileFinderLib;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateFileFinderLibTests
{
    [TestClass()]
    public class DuplicateFileFinderTests
    {
        public TestContext TestContext { get; set; } = null!;

        [TestMethod()]
        public void ScanLocationTest_EmptyDir()
        {
            var testDir = TestData.Path + "EmptyDir";
            var expectedFile = TestData.Path + TestContext.TestName + "_expected.csv";
            var actualFile = TestData.Path + TestContext.TestName + "_actual.csv";

            DuplicateFileFinder dupFileFinder = new();
            dupFileFinder.ScanLocation(testDir).Wait();

            StreamWriter sw = new StreamWriter(actualFile);
            dupFileFinder.ExportToCsv(sw);
            sw.Close();

            Assert.IsTrue(TestData.CsvFileCompare(expectedFile, actualFile));
        }

        [TestMethod()]
        public void ScanLocationTest()
        {
            var testDir = TestData.Path + "TestDir2";
            var expectedFile = TestData.Path + TestContext.TestName + "_expected.csv";
            var actualFile = TestData.Path + TestContext.TestName + "_actual.csv";

            DuplicateFileFinder dupFileFinder = new();
            dupFileFinder.ScanLocation(testDir).Wait();

            StreamWriter sw = new StreamWriter(actualFile);
            dupFileFinder.ExportToCsv(sw);
            sw.Close();

            Assert.IsTrue(TestData.CsvFileCompare(expectedFile, actualFile));
        }

        [TestMethod()]
        public void ScanLocationTest_twice()
        {
            var testDir2 = TestData.Path + "TestDir2";
            var expectedFile = TestData.Path + "ScanLocationTest_expected.csv";
            var actualFile = TestData.Path + TestContext.TestName + "_actual.csv";

            DuplicateFileFinder dupFileFinder = new();
            dupFileFinder.ScanLocation(testDir2).Wait();
            dupFileFinder.ScanLocation(testDir2).Wait();

            StreamWriter sw = new StreamWriter(actualFile);
            dupFileFinder.ExportToCsv(sw);
            sw.Close();

            Assert.IsTrue(TestData.CsvFileCompare(expectedFile, actualFile));
        }

        [TestMethod()]
        public void ExportToCsvTest()
        {
            DuplicateFileFinder dupFileFinder = new();

            StringWriter sw = new StringWriter();
            dupFileFinder.ExportToCsv(sw);
            Assert.AreEqual("File/Folder,Path,Size,File Count,Extension,MD5,Group"+Environment.NewLine, sw.ToString());
        }

        [TestMethod()]
        public void ImportFromCsvTest()
        {
            DuplicateFileFinder dupFileFinder = new();

            var expectedFile = TestData.Path + "ScanLocationTest_expected.csv";
            var actualFile = TestData.Path + TestContext.TestName + "_actual.csv";

            StreamReader sr = new StreamReader(expectedFile);
            dupFileFinder.ImportFromCsv(sr);
            sr.Close();

            StreamWriter sw = new StreamWriter(actualFile);
            dupFileFinder.ExportToCsv(sw);
            sw.Close();

            Assert.IsTrue(TestData.CsvFileCompare(expectedFile, actualFile));
        }
    }
}