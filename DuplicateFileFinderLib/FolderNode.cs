using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using NLog;

namespace DuplicateFileFinderLib;

public class FolderNode : FileSystemNode
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public int AggregateFileCount { get; private set; }

    public int AggregateFolderCount { get; protected set; }

    public ReadOnlyCollection<FolderNode> SubFoldersContainingDuplicates => new(SubFolders.Where(f => f.ContainsDuplicates).ToList());

    public FolderNode(string path) : base(path)
    {
        AggregateFolderCount = 1;
    }

    internal FolderNode(CsvRowData rowInfo) : this(rowInfo.Path)
    {
        Size = rowInfo.Size;
        AggregateFileCount = rowInfo.FileCount;
        Checksum = rowInfo.Checksum;
    }

    // recursively traverse folder structure and apply task
    public async Task TraverseFolders(Func<FolderNode, Task>? down = null, Func<FolderNode, Task>? up = null)
    {
        if (down != null) await down(this);

        foreach (var subDir in SubFolders)
            await subDir.TraverseFolders(down, up);

        if (up != null) await up(this);
    }

    public override string Name
    {
        get
        {
            return Path.Split(
                new[] {System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar},
                StringSplitOptions.RemoveEmptyEntries).Last();

        }
    }

    protected override void WriteCsvEntry(TextWriter writer)
    {
        writer.WriteLine("Folder,\"{0}\",{1},{2},,{3}", Path, Size, AggregateFileCount, Checksum);
    }

    public void PopulateFolderInfo()
    {
        children.Clear(); 
        try
        {
            var di = new DirectoryInfo(Path);

            children.AddRange(di.GetDirectories().OrderBy(f => f.Name).Select(d => new FolderNode(d.FullName)));

            children.AddRange(di.GetFiles().OrderBy(f => f.Name).Select(f => new FileNode(f.FullName)));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            Logger.Info(ex.Message);
        }
    }

    public void UpdateFolderStats()
    {
        AggregateFileCount = Files.Count + SubFolders.Sum(s => s.AggregateFileCount);
        Size = children.Sum(n => n.Size);
        AggregateFolderCount = SubFolders.Count + SubFolders.Sum(s => s.AggregateFolderCount);
    }

    // compute MD5 hash of folder by computer hash of the concatenation of file and subfolder hashes
    // Note: files and subfolders ARE stored in name sorted order so duplicate folders should have the 
    // same AggregateHashStr
    public void ComputeChecksum()
    {
        string aggregateHashStr = "";

        foreach (var file in Files)
        {
            if (file.Checksum == string.Empty)
                return; // abort

            aggregateHashStr += file.Checksum;
        }

        foreach (var folder in SubFolders)
        {
            if (folder.Checksum == string.Empty)
                return; // abort

            aggregateHashStr += folder.Checksum;
        }

        using var md5 = MD5.Create();
        {
            byte[] inputBytes = Encoding.ASCII.GetBytes(aggregateHashStr);
            byte[] hashBytes = md5.ComputeHash(inputBytes);

            Checksum = string.Concat(hashBytes.Select(x => x.ToString("X2")));
        }
    }

    public Task UpdateDuplicateFileStats()
    {
        DuplicateFileCount = 0;
        SizeOfDuplicates = 0;

        foreach (var folder in SubFolders)
        {
            DuplicateFileCount += folder.DuplicateFileCount;
            SizeOfDuplicates += folder.SizeOfDuplicates;
        }

        foreach (var file in Files)
        {
            if (!(file.Group?.DuplicateCount > 1)) continue;
            DuplicateFileCount++;
            SizeOfDuplicates += file.Size;
        }

        return Task.CompletedTask;
    }

    public long SizeOfDuplicates { get; private set; }

    public int DuplicateFileCount { get; private set; }

    public bool ContainsDuplicates => DuplicateFileCount > 0;
}