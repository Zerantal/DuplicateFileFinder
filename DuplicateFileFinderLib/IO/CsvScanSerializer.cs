// DuplicateFileFinderLib/IO/CsvScanSerializer.cs

using System.Collections.ObjectModel;
using System.Text;
using DuplicateFileFinderLib.Tree;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.IO;

public interface IScanSerializer
{
    void Export(RootNode root, TextWriter writer);
    void ImportInto(RootNode root, TextReader reader);
}

public sealed class CsvScanSerializer : IScanSerializer
{
    internal static readonly ReadOnlyDictionary<CsvFields, int> FieldMap =
        new(Enum.GetValues<CsvFields>().Select((v, i) => new { Value = v, Index = i })
            .ToDictionary(x => x.Value, x => x.Index));

    internal static string CsvHeaderRow => string.Join(',', Enum.GetNames<CsvFields>());


    public void Export(RootNode root, TextWriter writer)
    {
        writer.WriteLine(CsvHeaderRow);
        root.WriteCsvEntries(writer);
    }

    public void ImportInto(RootNode root, TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var line = reader.ReadLine() ?? throw new InvalidFormatException("Empty File");

        var expectedHeadings = Enum.GetNames<CsvFields>();
        var header = line.Split(',');
        if (!header.SequenceEqual(expectedHeadings))
            throw new InvalidFormatException(
                $"Required headings not detected. Expected: {string.Join(',', expectedHeadings)}");

        Dictionary<string, FileNode> files = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FolderNode> folders = new(StringComparer.OrdinalIgnoreCase);

        var row = 1;
        while ((line = reader.ReadLine()) != null)
        {
            if (!TryParseCsvRow(line, out var rowInfo) || rowInfo == null)
                throw new InvalidFormatException("Error parsing data on row " + row);

            if (rowInfo.Kind == KindEnum.File)
                files[rowInfo.Path] = new FileNode(rowInfo);
            else
                folders[rowInfo.Path] = new FolderNode(rowInfo);

            row++;
        }

        ConstructFolderTree(root, folders, files);
    }

    // ---------- parsing helpers (from your class, unchanged in behavior)

    private static List<string> SplitCsvRespectingQuotes(string line)
    {
        var fields = new List<string>(FieldMap.Count);
        var sb = new StringBuilder(line.Length);
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
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

    internal static bool TryParseCsvRow(string line, out CsvRowData? rowInfo)
    {
        rowInfo = null;

        var fields = SplitCsvRespectingQuotes(line);
        if (fields.Count != FieldMap.Count) return false;

        foreach (var idx in FieldMap.Values)
            fields[idx] = Unquote(fields[idx]);

        if (!Enum.TryParse(GetField(fields, CsvFields.Kind), out KindEnum kind))
            return false;

        if (!long.TryParse(GetField(fields, CsvFields.Size), out var size)) return false;

        var fileCount = 0;
        if (kind == KindEnum.Folder && !int.TryParse(GetField(fields, CsvFields.FileCount), out fileCount))
            return false;

        if (!int.TryParse(GetField(fields, CsvFields.Group), out var group)) return false;

        if (!DateTimeOffset.TryParse(GetField(fields, CsvFields.CreationTime), out var creationTime)) return false;
        
        rowInfo = new CsvRowData
        {
            Kind = kind,
            Path = GetField(fields, CsvFields.Path),
            CreationTime = creationTime,
            Size = size,
            FileCount = fileCount,
            Checksum = GetField(fields, CsvFields.Checksum),
            Group = group
        };
        return true;
    }

    private static void ConstructFolderTree(RootNode root,
        Dictionary<string, FolderNode> folders,
        Dictionary<string, FileNode> files)
    {
        foreach (var (folderPath, folderNode) in folders)
        {
            var parent = PathUtils.GetParentPath(folderPath);
            if (parent != null && folders.TryGetValue(parent, out var parentNode))
                parentNode.AddFileSystemNode(folderNode);
            else
                root.AddFileSystemNode(folderNode);
        }

        foreach (var (filePath, fileNode) in files)
        {
            var parentFolderPath = PathUtils.GetParentPath(filePath);
            if (parentFolderPath != null && folders.TryGetValue(parentFolderPath, out var parentFolderNode))
                parentFolderNode.AddFileSystemNode(fileNode);
            else
                throw new InvalidFormatException($"Missing parent folder entry for file in CSV: {filePath}");
        }
    }

    private static string GetField(List<string> row, CsvFields field)
    {
        return row[FieldMap[field]];
    }

    internal static void SetField(string[] rowValues, CsvFields field, string value)
    {
        rowValues[FieldMap[field]] = value;
    }

    internal enum CsvFields
    {
        Kind = 0,
        Path,
        CreationTime,
        Size,
        FileCount,
        Checksum,
        Group
    }
}