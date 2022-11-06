

// TODO:
// decouple dir traversal from MD5 computation and multi-thread MD5 computation using channels?

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Channels;
using NLog;

// ReSharper disable UnusedMember.Global

namespace DuplicateFileFinderLib
{
    public class DuplicateFileFinder
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly RootNode _root = new RootNode();

        private readonly FileSystemGroups _groups = new();

        private readonly Dictionary<long, int> _fileSizes = new();   // filesize => count

        public DuplicateFileFinder()
        {
        }

        private async Task BuildFolderStructure(FolderNode folder, IProgress<DuplicateFileFinderProgressReport>? progressIndicator)
        {
            double progress = 0.0;
            Stack<double> progressSlices = new();
            progressSlices.Push(1.0);

#pragma warning disable CS1998
            async Task ScanFolderStructure(FolderNode f)
#pragma warning restore CS1998
            {
                f.PopulateFolderInfo();
                if (f.SubFolders.Count != 0)
                    progressSlices.Push(progressSlices.Peek() / f.SubFolders.Count);
            }

            async Task BuildFileStats(FolderNode f)
            {
                f.UpdateFolderStats();

                if (f.SubFolders.Count != 0)
                    progressSlices.Pop();
                else
                {
                    var slice = progressSlices.Peek();

                    progress += slice;
                    progressIndicator?.Report(new DuplicateFileFinderProgressReport(progress));
                }

                await Task.Yield(); // This may not actually keep UI responsive in GUI context due to how their synchronization context prioritizes work
            }
            
            await folder.TraverseFolders(ScanFolderStructure, BuildFileStats);
        }

        private async Task BuildDirTrees(FolderNode folder, Progress<DuplicateFileFinderProgressReport>? progressIndicator)
        {
            Stopwatch watch = new Stopwatch();
            watch.Start();

            await BuildFolderStructure(folder, progressIndicator);

            watch.Stop();
            
            Logger.Info("Scanned directory tree(s) in {0} ms", watch.ElapsedMilliseconds);
        }

        public async Task ScanLocation(string location, Progress<DuplicateFileFinderProgressReport>? progressIndicator = null, bool recomputeHashes = false)
        {
            var folder = new FolderNode(location);
            if (LocationAlreadyAdded(folder))
                return;
            _root.AddFileSystemNode(folder);

            // build directory tree
            (progressIndicator as IProgress<DuplicateFileFinderProgressReport>)?.Report(new DuplicateFileFinderProgressReport("Scanning directory structure..."));
            await BuildDirTrees(folder, progressIndicator);

            AddToFileSizesDictionary(folder);

            // compute hashes
            (progressIndicator as IProgress<DuplicateFileFinderProgressReport>)?.Report(new DuplicateFileFinderProgressReport("Computing MD5 hashes of files/folders..."));
            await ComputeChecksums(progressIndicator);

            // build duplicate file/folder groups
            (progressIndicator as IProgress<DuplicateFileFinderProgressReport>)?.Report(new DuplicateFileFinderProgressReport("Grouping duplicate files/folders..."));
            await _groups.AssignGroups(_root);

            (progressIndicator as IProgress<DuplicateFileFinderProgressReport>)?.Report(new DuplicateFileFinderProgressReport());
        }

        private bool LocationAlreadyAdded(FolderNode folder)
        {
            bool result = false;

            foreach (var location in _root.SubFolders)
            {
                if (!result)
                {
                    location.TraverseFolders((f) =>
                    {
                        if (result) return Task.CompletedTask;
                        if (f.Path == folder.Path)
                            result = true;

                        return Task.CompletedTask;
                    }).Wait();
                }
            }

            return result;
        }

        // compile dictionary of file size counts
        private void AddToFileSizesDictionary(FolderNode folder)
        {
            folder.TraverseFolders((f) =>
            {
                foreach (var file in f.Files)
                {
                    if (_fileSizes.ContainsKey(file.Size))
                            _fileSizes[file.Size]++;
                    else
                        _fileSizes[file.Size] = 1;
                }

                return Task.CompletedTask;
            }).Wait();
        }

        // if file/folder already has a checksum, it doesn't re-calculate
        private async Task ComputeChecksums(IProgress<DuplicateFileFinderProgressReport>? progressIndicator)
        {
            Stopwatch timer = new();
            timer.Start();

            var ch = Channel.CreateBounded<FileNode>(4000);

            // consumer tasks
            Task[] md5ComputeTasks = new Task[Environment.ProcessorCount];
            for (int i = 0; i < md5ComputeTasks.Length; i++)
            {
                md5ComputeTasks[i] = Task.Run(async () =>
                {
                    while (await ch.Reader.WaitToReadAsync())
                    {
                        while(ch.Reader.TryRead(out var file))
                            await file.ComputeChecksum();
                    }
                });
            }

            // producer task
            int fileProcessedCounter = 0;
            var fileCount = _root.SubFolders.Sum((l) => l.AggregateFileCount);
            foreach (var location in _root.SubFolders)
            {
                await location.TraverseFolders(async (folder) =>
                {
                    foreach (var f in folder.Files)
                    {
                        if (_fileSizes[f.Size] != 1)
                        {
                            if (f.Checksum == "")
                                await ch.Writer.WriteAsync(f);
                        }

                        fileProcessedCounter++;
                        progressIndicator?.Report(new DuplicateFileFinderProgressReport((double)fileProcessedCounter / fileCount));
                    }
                });
            }

            ch.Writer.Complete();

            await Task.WhenAll(md5ComputeTasks);

            // computer folder hashes (only folders where all sub objects have been hashed)
            foreach (var location in _root.SubFolders)
                location.TraverseFolders(up: (folder) =>
                {
                    if (folder.Checksum == "")
                        folder.ComputeChecksum();
                    return Task.CompletedTask;
                }).Wait();

            timer.Stop();
            Logger.Info("Hash computation completed in {0} ms", timer.ElapsedMilliseconds);
        }

        public void ExportToCsv(TextWriter writer)
        {
            writer.WriteLine("File/Folder,Path,Size,File Count,Extension,MD5,Group");
            _root.WriteCsvEntries(writer);
        }



        // will clear existing data.
        // TODO: maybe add to existing data?
        public void ImportFromCsv(TextReader reader)
        {
            const int numColumnsRequired = 7;

            var line = reader.ReadLine();
            if (line == null) throw new InvalidFormatException("Empty File");
            var fields = line.Split(',');
            if (fields.Length < numColumnsRequired)
                throw new InvalidFormatException("Insufficient number of headings detected");

            Dictionary<string, FileNode> fileDictionary = new();
            Dictionary<string, FolderNode> folderDictionary = new();

            int row = 1;
            while ((line = reader.ReadLine()) != null)
            {
                if (tryParseCsvRow(line, out var rowInfo))
                {
                    Debug.Assert(rowInfo != null, nameof(rowInfo) + " != null");
                    if (rowInfo.IsFile)
                        fileDictionary[rowInfo.Path] = new FileNode(rowInfo);
                    else
                        folderDictionary[rowInfo.Path] = new FolderNode(rowInfo);

                    row++;
                    continue;
                }

                throw new InvalidFormatException("Error parsing data on row " + row);
            }

            ConstructFolderTree(folderDictionary, fileDictionary);

        }

        private void ConstructFolderTree(Dictionary<string, FolderNode> folderDictionary, Dictionary<string, FileNode> fileDictionary)
        {
            foreach (var (folderPath, folderNode) in folderDictionary)
            {
                string parentPath = Path.GetFullPath(folderPath + @"\..");
                if (folderDictionary.TryGetValue(parentPath, out var parentNode))
                    parentNode.AddFileSystemNode(folderNode);
                else
                    _root.AddFileSystemNode(folderNode);
            }

            foreach (var (filePath, fileNode) in fileDictionary)
            {
                string parentFolderPath = Path.GetFullPath(filePath + @"\..");
                if (folderDictionary.TryGetValue(parentFolderPath, out var parentFolderNode))
                    parentFolderNode.AddFileSystemNode(fileNode);
                else
                {
                    Logger.Log(LogLevel.Warn, @"Can't find parent folder entry for file when importing csv: " + filePath);
                }
            }

        }

        private bool tryParseCsvRow(string line, out CsvRowData? rowInfo)
        {
            var fields = line.Split(',');
            rowInfo = null;

            if (fields.Length != 7)
                return false;

            
            if (!(fields[0] == "File" || fields[0] == "Folder")) return false;
            bool isFile = fields[0] == "File";
            
            if (!long.TryParse(fields[2], out var size)) return false;

            var fileCount = 0;
            if (!isFile && !int.TryParse(fields[3], out fileCount)) return false;

            if (!int.TryParse(fields[6], out var group)) return false;

            rowInfo = new CsvRowData
            {
                IsFile = isFile, Path = fields[1].Replace("\"", ""), Size = size, FileCount = fileCount, Extension = fields[4].Replace("\"", ""),
                Checksum = fields[5], Group = group
            };

            return true;
        }

    }
}
