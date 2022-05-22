using System.Diagnostics;
using NLog;

namespace DuplicateFileFinderLib;

internal class FileSystemGroups
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // ReSharper disable once CollectionNeverQueried.Local
    private readonly Dictionary<int, Tuple<string, bool>> _groupHashes = new(); // groupNum => (md5, IsFile)
    private readonly Dictionary<string, FileGroup> _fileGroups = new();
    private readonly Dictionary<string, FolderGroup> _folderGroup = new();

    public async Task AssignGroups(FolderNode folder)
    {
        _groupHashes.Clear();
        _fileGroups.Clear();
        _folderGroup.Clear();

        Stopwatch watch = new Stopwatch();
        watch.Start();

#pragma warning disable CS1998
        await folder.TraverseFolders(async f =>
#pragma warning restore CS1998
        {
            AssignFolderToGroup(f);

            AssignFilesToGroups(f);
        });

        watch.Stop();
        Logger.Info("Group assignment completed in {0} ms", watch.ElapsedMilliseconds);
    }

    private void AssignFilesToGroups(FileSystemNode folder)
    {
        // assign files
        foreach (var f in folder.Files)
        {
            if (f.Checksum == string.Empty) continue;

            if (!_fileGroups.ContainsKey(f.Checksum))
            {
                FileGroup grp = new FileGroup(f);
                _fileGroups[f.Checksum] = grp;
                _groupHashes[grp.Id] = new Tuple<string, bool>(f.Checksum, true);
            }
            else
            {
                var grp = _fileGroups[f.Checksum];
                grp.AddFile(f);
            }
        }
    }

    private void AssignFolderToGroup(FolderNode folder)
    {
        if (folder.Checksum == string.Empty)
            return;
        
        // assign folder
        if (!_folderGroup.ContainsKey(folder.Checksum))
        {
            FolderGroup grp = new FolderGroup(folder);
            _folderGroup[folder.Checksum] = grp;
            _groupHashes[grp.Id] = new Tuple<string, bool>(folder.Checksum, false);
        }
        else
        {
            var grp = _folderGroup[folder.Checksum];
            grp.AddFolder(folder);
        }
    }

    public int DuplicateFiles 
    {
        get
        {
            return _fileGroups.Values.Where(fileGrp => fileGrp.DuplicateCount > 1).Sum(fileGrp => fileGrp.DuplicateCount - 1);
        }
    }

    public long AggregateDuplicateFilesSize
    {
        get
        {
            return _fileGroups.Values.Where(fileGrp => fileGrp.DuplicateCount > 1).Sum(fileGrp => fileGrp.Files.First().Size * (fileGrp.DuplicateCount - 1));
        }
    }
}