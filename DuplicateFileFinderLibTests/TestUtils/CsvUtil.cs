using System;
using System.Collections.Generic;
using System.Linq;
using DuplicateFileFinderLib.IO;

namespace DuplicateFileFinderLibTests.TestUtils;

internal class CsvUtil
{
    // internal record CsvRow(string Kind, string Path, long Size, string Checksum, int Group);
    
    internal static CsvRowData[] ReadCsvRows(string csv)
    {
        // Split lines and skip the header
        var lines = csv
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .ToArray();
    
        var parsed = new List<CsvRowData>();

        foreach (var line in lines)
        {
            if (CsvScanSerializer.TryParseCsvRow(line, out var row))
                parsed.Add(row!);
        }

    
        return [.. parsed];
    }
}