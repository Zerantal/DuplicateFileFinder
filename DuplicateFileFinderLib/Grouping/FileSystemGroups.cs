using DuplicateFileFinderLib.Tree;

namespace DuplicateFileFinderLib.Grouping;

internal class FileSystemGroups
{
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

    public async Task AssignGroups(FolderNode folder,
        Action<long>? onProgress = null,
        CancellationToken ct = default)
    {
        // 1) Pre-count total units of work (folders + files) for determinate progress.
        long total = 0;
        await folder.TraverseFolders(f =>
        {
            ct.ThrowIfCancellationRequested();
            total += 1; // the folder itself
            total += f.Files.Count; // all files in this folder
            return Task.CompletedTask;
        });
        total = Math.Max(1, total);

        // 2) Second pass: assign groups and report progress.
        long processed = 0;
        // ReSharper disable once InconsistentNaming
        const int TICK = 16384;     // interval at which to send progress updates
        
        await folder.TraverseFolders(f =>
        {
            ct.ThrowIfCancellationRequested();

            // folder group
            AssignFolderToGroup(f);
            processed++;
            if ((processed & (TICK - 1)) == 0)
                onProgress?.Invoke(processed);

            // files
            foreach (var file in f.Files)
            {
                ct.ThrowIfCancellationRequested();
                AssignFileToGroup(file);
                processed++;
                if ((processed & (TICK - 1)) == 0)
                    onProgress?.Invoke(processed);
            }

            return Task.CompletedTask;
        });

        onProgress?.Invoke(total);
    }

    private void AssignFileToGroup(FileNode f)
    {
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