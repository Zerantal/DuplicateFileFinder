using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DuplicateFileFinderLibTests;

[ExcludeFromCodeCoverage]
internal class TestUtil
{
    
    private static bool CsvTextReaderCompare(TextReader expected, TextReader actual)
    {
        // Open the two files.
        string? l1;
        string? l2;
        do
        {
            l1 = expected.ReadLine();
            l2 = actual.ReadLine();

            if (l1 == null && l2 == null) continue;
            if (l1 == null || l2 == null) return false;

            var va1 = l1.Split(',');
            var va2 = l2.Split(',');
            if (va1.Length != va2.Length)
                return false;

            for (int i = 0; i < va1.Length; i++)
            {
                //if (i == 1 && va1[0] != "File/Folder")
                //{
                //    // Try to normalise path string
                //    va1[1] = Path.GetFullPath(va1[1].Trim('\"'));
                //    va2[1] = va2[1].Trim('\\', '\"');

                //}
                
                if (va1[i] != va2[i])
                    return false;
            }
        } while (l1 != null && l2 != null);

        return true;
    }

    public static bool CsvStringCompare(string expected, string actual)
    {
        using StringReader e = new(expected), a = new(actual);
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