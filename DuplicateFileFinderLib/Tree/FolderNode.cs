using System.Security.Cryptography;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Util;

namespace DuplicateFileFinderLib.Tree;

public class FolderNode(string path, DateTimeOffset creationTime = default) : FileSystemNode(path, creationTime)
{
    internal FolderNode(CsvRowData rowInfo) : this(rowInfo.Path, rowInfo.CreationTime)
    {
        Size = rowInfo.Size;
        AggregateFileCount = rowInfo.FileCount;
        ChecksumHex = rowInfo.Checksum!;
        Group = rowInfo.Group;
    }

    public int AggregateFileCount { get; internal set; }

    public int AggregateFolderCount { get; protected internal set; } = 1;

    public FolderNode DeepCloneSubtree()
    {
        var clone = new FolderNode(Path)
        {
            AggregateFileCount = AggregateFileCount,
            AggregateFolderCount = AggregateFolderCount,
            Size = Size,
            Group = Group,
            ChecksumBytes = ChecksumBytes is { Length: > 0 } b ? (byte[])b.Clone() : null
        };

        // files
        foreach (var f in Files)
        {
            var nf = new FileNode(f.Path, f.Size, f.CreationTime)
            {
                Group = f.Group,
                ChecksumBytes = f.ChecksumBytes is { Length: > 0 } fb ? (byte[])fb.Clone() : null
            };
            clone.AddFileSystemNode(nf);
        }

        // subfolders
        foreach (var d in SubFolders)
            clone.AddFileSystemNode(d.DeepCloneSubtree());
        
        return clone;
    }

    public Task RecomputeSubtreeAggregatesAsync()
    {
        return TraverseFolders(
            down: null,
            up: f => { f.UpdateFolderStats(); return Task.CompletedTask; }
        );
    }
    
    internal FolderNode? FindSubFolderByPath(string fullPath)
    {
        return SubFolders.FirstOrDefault(sf =>
            string.Equals(PathUtils.NormalizePath(sf.Path),
                PathUtils.NormalizePath(fullPath),
                StringComparison.OrdinalIgnoreCase));
    }

    // recursively traverse folder structure and apply task
    public async Task TraverseFolders(Func<FolderNode, Task>? down = null, Func<FolderNode, Task>? up = null)
    {
        if (down != null) await down(this);

        foreach (var subDir in SubFolders)
            await subDir.TraverseFolders(down, up);

        if (up != null) await up(this);
    }

    protected override void WriteCsvEntry(TextWriter writer)
    {
        var fields = new string[CsvScanSerializer.FieldMap.Count];
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.Kind, nameof(KindEnum.Folder));
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.Path, $@"""{Path}""");
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.CreationTime, CreationTime.ToString());
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.Size, Size.ToString());
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.FileCount, AggregateFileCount.ToString());
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.Checksum, ChecksumHex);
        CsvScanSerializer.SetField(fields, CsvScanSerializer.CsvFields.Group, Group.ToString());

        writer.WriteLine(string.Join(',', fields));
    }

    public virtual void UpdateFolderStats()
    {
        AggregateFileCount = Files.Count + SubFolders.Sum(s => s.AggregateFileCount);
        Size = Children.Sum(n => n.Size);
        AggregateFolderCount = 1 + SubFolders.Sum(s => s.AggregateFolderCount);
    }

    // compute MD5 hash of folder by computer hash of the concatenation of file and subfolder hashes            
    public void ComputeChecksum(CancellationToken token = default)
    {
        // If any child lacks a checksum, we can't form a stable folder hash yet.            
        if (Children.Any(f => f.ChecksumBytes == null))
            return;

        var combinedChecksums = Children.Select(f => f.ChecksumBytes!).ToList();

        combinedChecksums.Sort(static (a, b) =>
        {
            var len = Math.Min(a.Length, b.Length);
            for (var i = 0; i < len; i++)
            {
                var cmp = a[i].CompareTo(b[i]);
                if (cmp != 0) return cmp;
            }

            return a.Length.CompareTo(b.Length);
        });


        using var md5 = MD5.Create();
        foreach (var h in combinedChecksums)
        {
            token.ThrowIfCancellationRequested();
            md5.TransformBlock(h, 0, h.Length, null, 0);
        }

        md5.TransformFinalBlock([], 0, 0);
        ChecksumBytes = md5.Hash;
    }
}