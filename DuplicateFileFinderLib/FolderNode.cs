using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using NLog;

namespace DuplicateFileFinderLib
{
    public class FolderNode
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public string Checksum { get; private set; } = string.Empty;



        public string FolderPath { get; internal set; }

        public long AggregateSize { get; internal set; }

        public int Group { get; internal set; } = -2;

        public int AggregateFileCount { get; internal set; }

        public int AggregateFolderCount { get; private set; }

        public ReadOnlyCollection<FileNode> Files => new(_files);

        public ReadOnlyCollection<FolderNode> SubFolders => new(_subFolders);

        private List<FileNode> _files;
        private List<FolderNode> _subFolders;

        public FolderNode(string path)
        {
            this.FolderPath = path ?? throw new ArgumentNullException(nameof(path));

            _files = new List<FileNode>();
            _subFolders = new List<FolderNode>();
            AggregateFolderCount = 1;
        }

        internal FolderNode(CsvRowData rowInfo) : this(rowInfo.Path)
        {
            AggregateSize = rowInfo.Size;
            AggregateFileCount = rowInfo.FileCount;
            Checksum = rowInfo.Checksum;
            Group = rowInfo.Group;
        }

        // recursively traverse folder structure and apply task
        public async Task TraverseFolders(Func<FolderNode, Task>? down = null, Func<FolderNode, Task>? up = null)
        {
            if (down != null) await down(this);

            foreach (var subDir in SubFolders)
                await subDir.TraverseFolders(down, up);

            if (up != null) await up(this);
        }

        public void WriteCsvEntries(TextWriter writer)
        {
            writer.WriteLine("Folder,\"{0}\",{1},{2},,{3},{4}", FolderPath, AggregateSize, AggregateFileCount, Checksum, Group);
            foreach (var f in Files)
                f.WritesCsvEntry(writer);

            foreach (var d in SubFolders)
                d.WriteCsvEntries(writer);
        }

        public void PopulateFolderInfo()
        {
            try
            {
                var di = new DirectoryInfo(FolderPath);
                _files = di.GetFiles("*.*").OrderBy(f => f.Name).Select(f => new FileNode(f.FullName)).ToList();
                _subFolders = di.GetDirectories().OrderBy(f => f.Name).Select(d => new FolderNode(d.FullName)).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                Logger.Info(ex.Message);
            }
        }

        public void UpdateFolderStats()
        {
            AggregateFileCount = Files.Count + SubFolders.Sum(s => s.AggregateFileCount);
            AggregateSize = Files.Sum(f => f.Size) + SubFolders.Sum(s => s.AggregateSize);
            AggregateFolderCount = SubFolders.Count + SubFolders.Sum(s => s.AggregateFolderCount);
        }

        // compute MD5 hash of folder by computer hash of the concatenation of file and subfolder hashes
        // Note: files and subfolders ARE stored in name sorted order so duplicate folders should have the 
        // same AggregateHashStr
        public void ComputeChecksum()
        {
            string aggregateHashStr = "";

            foreach (var file in _files)
            {
                if (file.Checksum == string.Empty)
                    return; // abort

                aggregateHashStr += file.Checksum;
            }

            foreach (var folder in _subFolders)
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
    }
}
