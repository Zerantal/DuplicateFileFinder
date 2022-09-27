using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;
using NLog.Targets;

namespace DuplicateFileFinderLibTests
{
    [ExcludeFromCodeCoverage]
    internal class TestDir
    {
        public string DirName { get; }

        private Dictionary<string, string> _testFilePaths;

        private List<TestDir> _subDirs;

        public TestDir(string dirName)
        {
            DirName = dirName;
            _testFilePaths = new Dictionary<string, string>();
            _subDirs = new List<TestDir>();

            try
            {
                if (Directory.Exists(dirName))
                    Directory.Delete(dirName, true);

                Directory.CreateDirectory(dirName);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        /// <summary>
        /// create test file in test dir with given file size and containing repeating instances of the supplied string
        /// </summary>
        /// <param name="testFileName">File name</param>
        /// <param name="fileSize">Size of file to create</param>
        /// <param name="fileData">data to fill file with</param>
        public TestDir CreateTestFile(string testFileName, int fileSize, string fileData)
        {
            string filePath = DirName + "\\" + testFileName;

            try
            {
                using var fs = File.Create(filePath);

                var data = new UTF8Encoding(true).GetBytes(fileData);
                int offset;
                for (offset = 0; offset < fileSize - data.Length; offset += data.Length)
                {
                    fs.Write(data, 0, data.Length);
                }
                if (offset < fileSize)
                    fs.Write(data, 0, fileSize - offset);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }

            _testFilePaths[testFileName] = filePath;

            return this;
        }

        public string GetFilePath(string testFileName)
        {
            return _testFilePaths[testFileName];
        }

        public TestDir CreateTestDir(string testDirName)
        {
            TestDir subDir = new TestDir(DirName + "\\" + testDirName);
            _subDirs.Add(subDir);
            return subDir;
        }
    }
}
