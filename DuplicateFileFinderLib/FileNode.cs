using System.Security.Cryptography;
using NLog;

namespace DuplicateFileFinderLib;

public class FileNode : FileSystemNode
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public string Extension => System.IO.Path.GetExtension(Path);

    // Signify that hash value of entire file is calculated. This value is false if a test hash has been calculated instead.
    public bool FullHashCalculated { get; private set; }

    public bool IsDuplicated
    {
        get
        {
            if (Group == null)
                return false;
            return Group.DuplicateCount > 1;
        }
    }

    public FileNode(string path) : base(path)
    {
        var fi = new FileInfo(path);
        Size = fi.Length;
        LastModifiedTime = fi.LastWriteTime;
    }

    internal FileNode(CsvRowData rowInfo) : base(rowInfo.Path)
    {
        Size = rowInfo.Size;
        Checksum = rowInfo.Checksum;
    }

    /// <summary>
    /// Asynchronously Compute checksum of file.
    /// </summary>
    /// <param name="cancelToken"></param>
    /// <param name="testSize"></param>
    /// <returns></returns>
    public async Task ComputeChecksum(CancellationToken cancelToken = default, int testSize = -1)
    {
        if (testSize == -1 || testSize >= Size)
            await ComputeFullChecksum(cancelToken);
        else
        {
            await ComputeTestChecksum(cancelToken, testSize);
        }
    }

    private async Task ComputeTestChecksum(CancellationToken cancelToken, int testSize)
    {
        FileStream? fileStream = null;
        using var md5 = MD5.Create();
        try
        {
            var fileData = new byte[testSize];
            fileStream = File.Open(Path, FileMode.Open);
            var result = await fileStream.ReadAsync(fileData, 0, testSize, cancelToken);
            if (result == testSize)
            {
                var hashBytes = md5.ComputeHash(fileData, 0, testSize);
                Checksum = string.Concat(hashBytes.Select(x => x.ToString("X2")));
                FullHashCalculated = false;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            Logger.Info(ex.Message);
        }
        finally
        {
            fileStream?.Close();
        }
    }

    private async Task ComputeFullChecksum(CancellationToken cancelToken)
    {
        using var md5 = MD5.Create();
        try
        {
            await using var stream = File.OpenRead(Path);
            var hashBytes = await md5.ComputeHashAsync(stream, cancelToken);

            Checksum = string.Concat(hashBytes.Select(x => x.ToString("X2")));
            FullHashCalculated = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            Logger.Info(ex.Message);
        }
    }

    public override string Name => System.IO.Path.GetFileName(Path);
    public DateTime LastModifiedTime { get; }

    protected override void WriteCsvEntry(TextWriter writer)
    {
        writer.WriteLine("File,\"{0}\",{1},,\"{2}\",{3}", Path, Size, Extension, Checksum);
    }

    public override void AddFileSystemNode(FileSystemNode node)
    {
        throw new InvalidOperationException("Can't add node to FileNode object");
    }
}