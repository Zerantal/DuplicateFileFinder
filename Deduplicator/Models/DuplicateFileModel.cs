using System.IO;
// ReSharper disable UnusedMember.Global

namespace DuplicateFileFinder.Models;

public class DuplicateFileModel : BaseObjectModel
{
    private readonly FileInfo _file;

    public DuplicateFileModel(long groupId, FileInfo file)
    {
        FileGroup = groupId;
        _file = file;
    }

    public string FileName => _file.Name;

    public long FileSize => _file.Length;

    public string CreationDate => _file.CreationTime.ToLongDateString();

    public string Folder => _file.DirectoryName;

    public long FileGroup { get; }
}