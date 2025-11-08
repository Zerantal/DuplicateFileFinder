using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DuplicateFileFinderLib.IO;

namespace DuplicateFileFinderLibTests.TestUtils;

public static class CsvSpec
{
    // Single source of truth for CSV header with CreationTimeUtc added.
    public static readonly string[] Header =
    [
        "Kind","Path", "CreationTime", "Size","FileCount","Checksum","Group"
    ];

    public static string HeaderLine => string.Join(',', Header);
}

public record CsvRow(
    KindEnum Kind,
    string Path,
    DateTimeOffset CreationTimeUtc,
    long Size,
    int? FileCount,
    string Checksum,
    int Group
);

public static class CsvTestUtil
{
    public static string Csv(params string[] lines)
        => string.Join('\n',  new[] { CsvSpec.HeaderLine}.Concat(lines));

    public static string CsvRowString(params string[] fieldValues)
        => string.Join(',', fieldValues);
    
    internal static CsvRow[] Parse(string csv)
    {
        var lines = csv.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return [];
        
        var head = Split(lines[0]);
        if (!head.SequenceEqual(CsvSpec.Header))
            throw new FormatException("Unexpected CSV header");
        
        var rows = new List<CsvRow>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            var f = Split(lines[i]);
            
            // indexes are stable thanks to CsvSpec.Header
            var kind = Enum.Parse<KindEnum>(f[0]);
            var path = Unquote(f[1]);
            var createdUtc = DateTimeOffset.Parse(f[2]);
            var size = long.Parse(f[3]);
            int? fileCount = int.TryParse(f[4], out var fc) ? fc : null;
            var checksum = Unquote(f[5]);
            var group = int.Parse(f[6]);

            rows.Add(new CsvRow(kind, path, createdUtc, size, fileCount, checksum, group));
        }
        return rows.ToArray();
    }

    // Same quote-aware splitter the product uses, simplified for tests
    private static List<string> Split(string line)
    {
        var fields = new List<string>(8);
        var sb = new StringBuilder(line.Length);
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                { sb.Append('"'); i++; }
                else { inQuotes = !inQuotes; }
            }
            else if (c == ',' && !inQuotes) { fields.Add(sb.ToString()); sb.Clear(); }
            else { sb.Append(c); }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static string Unquote(string s)
    {
        if (s is ['"', _, ..] && s[^1] == '"')
            return s.Substring(1, s.Length - 2).Replace("\"\"", "\"");
        return s.Replace("\"\"", "\"");
    }
}