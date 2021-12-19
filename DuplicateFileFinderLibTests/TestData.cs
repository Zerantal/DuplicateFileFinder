using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateFileFinderLibTests
{
    internal class TestData
    {
        public static string Path = "TestData\\";

        private static bool CsvTextReaderCompare(TextReader expected, TextReader actual)
        {
            // Open the two files.
            string? l1;
            string? l2;
            do
            {
                l1 = expected.ReadLine();
                l2 = actual.ReadLine();

                if (l1 != null && l2 != null)
                {
                    var va1 = l1.Split(',');
                    var va2 = l2.Split(',');
                    if (va1.Length != va2.Length)
                        return false;

                    for (int i = 0; i < va1.Length; i++)
                    {
                        if (i == 1)
                        {
                            // convert path to relative path
                            int idx = va1[1].LastIndexOf(Path, StringComparison.Ordinal);
                            if (idx != -1)
                                va1[1] = va1[1].Substring(idx);
                            idx = va2[1].LastIndexOf(Path, StringComparison.Ordinal);
                            if (idx != -1)
                                va2[1] = va2[1].Substring(idx);
                        }
                        if (va1[i] != va2[i])
                            return false;
                    }

                }
            } while (l1 != null && l2 != null);

            return true;
        }

        public static bool CsvStringCompare(string expected, string actual)
        {
            using StringReader e = new StringReader(expected), a = new StringReader(actual);
            return CsvTextReaderCompare(e, a);
        }

        public static bool CsvFileCompare(string expectedFile, string actualFile)
        {
            // Determine if the same file was referenced two times.
            if (expectedFile == actualFile)
            {
                // Return true to indicate that the files are the same.
                return true;
            }

            // Open the two files.
            using StreamReader expected = new(expectedFile), actual = new(actualFile);
            return CsvTextReaderCompare(expected, actual);
            
        }

    }


}
