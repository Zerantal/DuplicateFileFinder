using System.IO;
using DuplicateFileFinderLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateFileFinderLibTests
{
    [TestClass()]
    public class FileNodeTests
    {
        [TestMethod()]
        public void FileNodeTest()
        {
            var file = new FileNode(TestData.Path + "TestDir1\\file2.txt");

            Assert.AreEqual(78, file.Size);
        }

        [TestMethod()]
        public void ComputeChecksumTest()
        {
            var file = new FileNode(TestData.Path + "TestDir1\\file2.txt");

            file.ComputeChecksum().Wait();
            Assert.AreEqual("a1a6c61c583a44697837bfe06267fd51".ToUpper(), file.Checksum);
        }

        [TestMethod()]
        public void WritesCsvEntryTest()
        {
            string expected =
                "File,\"\\TestData\\TestDir1\\file2.txt\",78,,\".txt\",A1A6C61C583A44697837BFE06267FD51, -1";

            var file = new FileNode(TestData.Path + "TestDir1\\file2.txt");

            file.ComputeChecksum().Wait();

            StringWriter sw = new StringWriter();
            file.WriteCsvEntries(sw);
            sw.Close();

            Assert.IsTrue(TestData.CsvStringCompare(expected, sw.ToString()));
        }
    }
}