using System.Security.Cryptography;
using DuplicateFileFinderLib.IO;
using NLog;

namespace DuplicateFileFinderLib.Tree;

public class FileNode : FileSystemNode
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    internal FileNode(string path, long size) : base(path)
    {
        Size = size;
        Group = -1;
    }

    internal FileNode(CsvRowData rowInfo) : base(rowInfo.Path)
    {
        Size = rowInfo.Size;
        ChecksumHex = rowInfo.Checksum!;
        Group = rowInfo.Group;
    }

    public string Extension => System.IO.Path.GetExtension(Path);

    public async Task ComputeChecksum(CancellationToken token = default)
    {
        try
        {
            await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            ChecksumBytes = await MD5.HashDataAsync(stream, token);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            Logger.Info(ex.Message);
        }
    }

    public FileNode DeepClone()
    {
        var clone = new FileNode(Path, Size);
        clone.CopyCommonFieldsTo(this); // copy from this → clone (we’ll fix target)
        // CopyCommonFieldsTo copies from the instance it’s called on; we need the reverse.
        // So replace that single line with:
        // clone.Size = this.Size;
        // clone.Group = this.Group;
        // clone.ChecksumBytes = this.ChecksumBytes is { Length: > 0 } b ? (byte[])b.Clone() : null;

        return clone;
    }

    protected override void WriteCsvEntry(TextWriter writer)
    {
        var fields = new string[CsvScanSerializer.FieldMap.Count];
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.Kind, nameof(KindEnum.File));
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.Path, $@"""{Path}""");
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.Size, Size.ToString());
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.Checksum, ChecksumHex);
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.Group, Group.ToString());

        writer.WriteLine(string.Join(',', fields));
    }

    public override void AddFileSystemNode(FileSystemNode node)
    {
        throw new InvalidOperationException("Can't add node to FileNode object");
    }
}