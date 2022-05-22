using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NLog;

// ReSharper disable UnusedMember.Global

namespace DuplicateFileFinderLib;

public class DuplicateFileFinder
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public readonly RootNode Root = new();

    private readonly FileSystemGroups _groups = new();

    private readonly Dictionary<long, int> _fileSizes = new();   // file size => count

    private const int ChecksumTestSize = 1000000;   

    private static async Task BuildFolderStructure(FolderNode folder, IProgress<DuplicateFileFinderProgressReport>? progressIndicator, CancellationToken cancelToken)
    {
        double progress = 0.0;
        Stack<double> progressSlices = new();
        progressSlices.Push(1.0);

#pragma warning disable CS1998
        async Task ScanFolderStructure(FolderNode f)
#pragma warning restore CS1998
        {
            if (cancelToken.IsCancellationRequested) return;
            f.PopulateFolderInfo();
            if (f.SubFolders.Count != 0)
                progressSlices.Push(progressSlices.Peek() / f.SubFolders.Count);
        }

        async Task BuildFileStats(FolderNode f)
        {
            if (cancelToken.IsCancellationRequested) return;

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

    private void CompileDuplicateFileStats()
    {
        Root.TraverseFolders(null,  f => f.UpdateDuplicateFileStats()).Wait();
    }


    private static async Task BuildDirTrees(FolderNode folder, IProgress<DuplicateFileFinderProgressReport>? progressIndicator, CancellationToken cancelToken)
    {
        Stopwatch watch = new Stopwatch();
        watch.Start();

        await BuildFolderStructure(folder, progressIndicator, cancelToken);

        watch.Stop();
            
        Logger.Info("Scanned directory tree(s) in {0} ms", watch.ElapsedMilliseconds);
    }

    public async Task ScanLocation(string location, Progress<DuplicateFileFinderProgressReport>? progressIndicator = null, bool recomputeHashes = false, CancellationToken cancelToken = default)
    {
        var folder = GetFolder(location);

        // build directory tree
        (progressIndicator as IProgress<DuplicateFileFinderProgressReport>)?.Report(
            new DuplicateFileFinderProgressReport("Scanning directory structure..."));
        await BuildDirTrees(folder, progressIndicator, cancelToken);
        await Root.TraverseFolders(up: f =>
        {
            if (cancelToken.IsCancellationRequested) return Task.CompletedTask;
            f.UpdateFolderStats();
            return Task.CompletedTask;
        }).WaitAsync(cancelToken);

        AddToFileSizesDictionary(folder);

        (progressIndicator as IProgress<DuplicateFileFinderProgressReport>)?.Report(
            new DuplicateFileFinderProgressReport("Computing test checksums of files/folders..."));
        // compute test hashes and group files
        await ComputeChecksums(progressIndicator, cancelToken, ChecksumTestSize);
        await _groups.AssignGroups(Root);
        CompileDuplicateFileStats();

        // compute full hashes of all files whose test hashes are equal, and re-group files
        (progressIndicator as IProgress<DuplicateFileFinderProgressReport>)?.Report(
            new DuplicateFileFinderProgressReport("Computing full checksums of files/folders..."));
        await ComputeChecksums(progressIndicator, cancelToken);
        await _groups.AssignGroups(Root);
        CompileDuplicateFileStats();


        (progressIndicator as IProgress<DuplicateFileFinderProgressReport>)?.Report(new DuplicateFileFinderProgressReport());
    }

    // get folder by path (creating the path if needed)
    private FolderNode GetFolder(string path)
    {
        var pathElements = path.Split(new[] {Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar},
            StringSplitOptions.RemoveEmptyEntries);

        FolderNode folder = Root;
        string tmpPath = "";
        foreach (string p in pathElements)
        {
            tmpPath = tmpPath + p + Path.DirectorySeparatorChar;
            if (folder.SubFolders.Any(f => f.Path == tmpPath))
            {
                folder = folder.SubFolders.Single(f => f.Path == tmpPath);
            }
            else
            {
                var newFolder = new FolderNode(tmpPath);
                folder.AddFileSystemNode(newFolder);
                folder = newFolder;
            }
        }

        return folder;
    }

    // compile dictionary of file size counts
    private void AddToFileSizesDictionary(FolderNode folder)
    {
        folder.TraverseFolders(f =>
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
    private async Task ComputeChecksums(IProgress<DuplicateFileFinderProgressReport>? progressIndicator, CancellationToken cancelToken, int testSize = -1)
    {
        Stopwatch timer = new();
        timer.Start();

        bool calculatingFullChecksum = testSize == -1;

        var ch = Channel.CreateBounded<FileNode>(1000);

        // consumer tasks
        Task[] md5ComputeTasks = new Task[Environment.ProcessorCount];
        for (int i = 0; i < md5ComputeTasks.Length; i++)
        {
            md5ComputeTasks[i] = Task.Run(async () =>
            {
                while (await ch.Reader.WaitToReadAsync(cancelToken))
                {
                    while(ch.Reader.TryRead(out var file))
                        await file.ComputeChecksum(cancelToken, testSize);
                }
            }, cancelToken);
        }

        // producer task
        int fileProcessedCounter = 0;
        var fileCount = Root.SubFolders.Sum(l => l.AggregateFileCount);
        foreach (var location in Root.SubFolders)
        {
            await location.TraverseFolders(async folder =>
            {
                if (cancelToken.IsCancellationRequested) return;
                foreach (var f in folder.Files)
                {
                    if (_fileSizes[f.Size] != 1)
                    {
                        if (calculatingFullChecksum && !f.FullHashCalculated && f.Size > ChecksumTestSize)
                        {
                            await ch.Writer.WriteAsync(f, cancelToken);
                        }
                        else
                        {
                            if (f.Checksum == "")
                                await ch.Writer.WriteAsync(f, cancelToken);
                        }
                    }

                    fileProcessedCounter++;
                    progressIndicator?.Report(new DuplicateFileFinderProgressReport((double)fileProcessedCounter / fileCount));
                }
            });
        }

        ch.Writer.Complete();

        await Task.WhenAll(md5ComputeTasks);

        // computer folder hashes (only folders where all sub objects have been hashed)
        foreach (var location in Root.SubFolders)
            await location.TraverseFolders(up: folder =>
            {
                if (folder.Checksum == "")
                    folder.ComputeChecksum();
                return Task.CompletedTask;
            }).WaitAsync(cancelToken);

        timer.Stop();
        Logger.Info("Hash computation completed in {0} ms", timer.ElapsedMilliseconds);
    }

    public void ExportToCsv(TextWriter writer)
    {
        writer.WriteLine("File/Folder,Path,Size,File Count,Extension,MD5");
        Root.WriteCsvEntries(writer);
    }

    // will clear existing data.
    // TODO: maybe add to existing data?
    public void ImportFromCsv(TextReader reader)
    {
        const int numColumnsRequired = 6;

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
            if (!TryParseCsvRow(line, out var rowInfo))
                throw new InvalidFormatException("Error parsing data on row " + row);
            Debug.Assert(rowInfo != null, nameof(rowInfo) + " != null");
            if (rowInfo.IsFile)
                fileDictionary[rowInfo.Path] = new FileNode(rowInfo);
            else
                folderDictionary[rowInfo.Path] = new FolderNode(rowInfo);

            row++;
        }

        ConstructFolderTree(folderDictionary, fileDictionary);

        _groups.AssignGroups(Root).Wait();
        CompileDuplicateFileStats();

    }

    private void ConstructFolderTree(Dictionary<string, FolderNode> folderDictionary, Dictionary<string, FileNode> fileDictionary)
    {
        foreach (var (folderPath, folderNode) in folderDictionary)
        {
            string parentPath = Path.GetFullPath(folderPath + @"\..");
            if (folderDictionary.TryGetValue(parentPath, out var parentNode) && parentPath != folderPath)
                parentNode.AddFileSystemNode(folderNode);
            else
                Root.AddFileSystemNode(folderNode);
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

        Root.TraverseFolders(null, f =>
        {
            f.UpdateFolderStats();
            return Task.CompletedTask;
        }).Wait();

    }

    private static bool TryParseCsvRow(string line, out CsvRowData? rowInfo)
    {
        var fields = new Regex("((?<=\")[^\"]*(?=\"(,|$)+)|(?<=,|^)[^,\"]*(?=,|$))").Matches(line).Select(m => m.Value).ToArray();

        rowInfo = null;

        if (fields.Length != 6)
            return false;

            
        if (!(fields[0] == "File" || fields[0] == "Folder")) return false;
        bool isFile = fields[0] == "File";
            
        if (!long.TryParse(fields[2], out var size)) return false;

        var fileCount = 0;
        if (!isFile && !int.TryParse(fields[3], out fileCount)) return false;

        rowInfo = new CsvRowData
        {
            IsFile = isFile, Path = fields[1].Replace("\"", ""), Size = size, FileCount = fileCount, Extension = fields[4].Replace("\"", ""),
            Checksum = fields[5]
        };

        return true;
    }

    public int LocationCount => Root.SubFolders.Count;

    public int DuplicateFileCount => _groups.DuplicateFiles;

    public long SpaceTakenByDuplicates => _groups.AggregateDuplicateFilesSize;
}