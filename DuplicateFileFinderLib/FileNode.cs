using System.Security.Cryptography;
using NLog;

namespace DuplicateFileFinderLib
{
    public class FileNode : FileSystemNode
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public string Extension => System.IO.Path.GetExtension(Path);

        public FileNode(string path) : base(path)
        {
            var fi = new FileInfo(path);
            Size = fi.Length;
            Group = -1;
        }

        internal FileNode(CsvRowData rowInfo) : base(rowInfo.Path)
        {
            Size = rowInfo.Size;
            Checksum = rowInfo.Checksum;
            Group = rowInfo.Group;
        }

        public async Task ComputeChecksum()
        {
            using var md5 = MD5.Create();
            try
            {
                await using var stream = File.OpenRead(Path);
                var hashBytes = await md5.ComputeHashAsync(stream);

                Checksum = string.Concat(hashBytes.Select(x => x.ToString("X2")));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                Logger.Info(ex.Message);
            }
        }

        protected override void WriteCsvEntry(TextWriter writer)
        {
            writer.WriteLine("File,\"{0}\",{1},,\"{2}\",{3}, {4}", Path, Size, Extension, Checksum, Group);
        }

        public override void AddFileSystemNode(FileSystemNode node)
        {
            throw new InvalidOperationException("Can't add node to FileNode object");
        }
    }
}
