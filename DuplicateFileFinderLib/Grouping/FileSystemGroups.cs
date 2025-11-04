using System.Diagnostics;
using DuplicateFileFinderLib.Tree;
using NLog;

namespace DuplicateFileFinderLib.Grouping;

internal class FileSystemGroups
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<string, FileGroup> _fileGroups = [];
    private readonly Dictionary<string, FolderGroup> _folderGroup = [];

    // ReSharper disable once CollectionNeverQueried.Local
    private readonly Dictionary<int, Tuple<string, bool>> _groupHashes = []; // groupNum => (md5, IsFile)

    private int _groupCounter;

    public FileSystemGroups()
    {
        // special groups for unhashable files/folders
        _groupHashes[-1] = new Tuple<string, bool>(string.Empty, true);
        _groupHashes[-2] = new Tuple<string, bool>(string.Empty, false);
        _fileGroups[string.Empty] = new FileGroup(-1);
        _folderGroup[string.Empty] = new FolderGroup(-2);
    }

    public async Task AssignGroups(FolderNode folder)
    {
        Stopwatch watch = new();
        watch.Start();

#pragma warning disable CS1998
        await folder.TraverseFolders(async folderNode =>
#pragma warning restore CS1998
        {
            AssignFolderToGroup(folderNode);

            AssignFilesToGroups(folderNode);
        });

        watch.Stop();
        Logger.Info("Group assignment completed in {0} ms", watch.ElapsedMilliseconds);
    }

    private void AssignFilesToGroups(FolderNode folder)
    {
        // assign files
        foreach (var f in folder.Files)
            if (f.ChecksumBytes == null || !_fileGroups.TryGetValue(f.ChecksumHex, out var grp1))
            {
                FileGroup grp = new(_groupCounter);
                _fileGroups[f.ChecksumHex] = grp;
                grp.AddFile(f);
                _groupHashes[_groupCounter] = new Tuple<string, bool>(f.ChecksumHex, true);
                _groupCounter++;
            }
            else
            {
                grp1.AddFile(f);
            }
    }

    private void AssignFolderToGroup(FolderNode folder)
    {
        // assign folder
        if (folder.ChecksumBytes == null || !_folderGroup.TryGetValue(folder.ChecksumHex, out var grp1))
        {
            FolderGroup grp = new(_groupCounter);
            grp.AddFolder(folder);
            _folderGroup[folder.ChecksumHex] = grp;
            _groupHashes[_groupCounter] = new Tuple<string, bool>(folder.ChecksumHex, false);
            _groupCounter++;
        }
        else
        {
            grp1.AddFolder(folder);
        }
    }
}