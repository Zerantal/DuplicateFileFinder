using System.Globalization;
using DuplicateFileFinder.Common;
using DuplicateFileFinderLib;

namespace DuplicateFileFinder.Models;

public class FileModel : BindableBase
{
    private readonly FileNode _file;

    public FileModel(FileNode file)
    {
        _file = file;
    }

    public string Name => _file.Name;

    public long Size => _file.Size;

    public string Path => System.IO.Path.GetDirectoryName(_file.Path);

    public string LastModifiedTime => _file.LastModifiedTime.ToString(CultureInfo.InvariantCulture);

    public bool IsFolder => false;
}