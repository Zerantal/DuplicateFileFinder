using System.Diagnostics;
using NLog;

namespace DuplicateFileFinderLib
{
    internal class FileSystemGroups
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private int _groupCounter;

        // ReSharper disable once CollectionNeverQueried.Local
        private readonly Dictionary<int, Tuple<string, bool>> _groupHashes = new(); // groupNum => (md5, IsFile)
        private readonly Dictionary<string, FileGroup> _fileGroups = new();
        private readonly Dictionary<string, FolderGroup> _folderGroup = new();

        public FileSystemGroups()
        {
            // special groups for unhashable files/folders
            _groupHashes[-1] = new Tuple<string, bool>(string.Empty, true);
            _groupHashes[-2] = new Tuple<string, bool>(string.Empty, false);
            _fileGroups[string.Empty] = new FileGroup(-1);
            _folderGroup[string.Empty] = new FolderGroup(-2);
        }

        public async Task AssignGroups(FolderNode [] locations)
        {
            Stopwatch watch = new Stopwatch();
            watch.Start();

            foreach (var loc in locations)
            {
#pragma warning disable CS1998
                await loc.TraverseFolders(async (folder) =>
#pragma warning restore CS1998
                {
                    AssignFolderToGroup(folder);

                    AssignFilesToGroups(folder);
                });
            }

            watch.Stop();
            Logger.Info("Group assignment completed in {0} ms", watch.ElapsedMilliseconds);
        }

        private void AssignFilesToGroups(FolderNode folder)
        {
            // assign files
            foreach (var f in folder.Files)
            {
                if (!_fileGroups.ContainsKey(f.Checksum))
                {
                    FileGroup grp = new FileGroup(_groupCounter);
                    _fileGroups[f.Checksum] = grp;
                    grp.AddFile(f);
                    _groupHashes[_groupCounter] = new Tuple<string, bool>(f.Checksum, true);
                    _groupCounter++;
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
            // assign folder
            if (!_folderGroup.ContainsKey(folder.Checksum))
            {
                FolderGroup grp = new FolderGroup(_groupCounter);
                grp.AddFolder(folder);
                _folderGroup[folder.Checksum] = grp;
                _groupHashes[_groupCounter] = new Tuple<string, bool>(folder.Checksum, false);
                _groupCounter++;
            }
            else
            {
                var grp = _folderGroup[folder.Checksum];
                grp.AddFolder(folder);
            }
        }
    }
}
