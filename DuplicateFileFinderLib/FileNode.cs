using System.Security.Cryptography;
using NLog;

namespace DuplicateFileFinderLib
{
    public class FileNode
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public string FilePath { get; internal set; }

        public long Size { get; init; }

        public string Extension { get; init; }

        public string Checksum { get; private set; } = string.Empty;

        public int Group { get; internal set; } = -1;

        public FileNode(string filePath)
        {
            this.FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));

            FileInfo fi = new FileInfo(filePath);

            Size = fi.Length;
            Extension = fi.Extension;
        }

        internal FileNode(CsvRowData rowInfo)
        {
            FilePath = rowInfo.Path;
            Size = rowInfo.Size;
            Extension = rowInfo.Extension;
            Checksum = rowInfo.Checksum;
            Group = rowInfo.Group;
        }

        public async Task ComputeChecksum()
        {
            using var md5 = MD5.Create();
            try
            {
                await using var stream = File.OpenRead(FilePath);
                var hashBytes = await md5.ComputeHashAsync(stream);

                Checksum = string.Concat(hashBytes.Select(x => x.ToString("X2")));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                Logger.Info(ex.Message);
            }
        }

        public void WritesCsvEntry(TextWriter writer)
        {
            writer.WriteLine("File,\"{0}\",{1},,\"{2}\",{3}, {4}", FilePath, Size, Extension, Checksum, Group);
        }
    }
}
