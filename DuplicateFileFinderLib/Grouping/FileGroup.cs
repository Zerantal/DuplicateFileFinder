using DuplicateFileFinderLib.Tree;

namespace DuplicateFileFinderLib.Grouping;

public class FileGroup(int fileGroup)
{
    // ReSharper disable once CollectionNeverQueried.Local
    private readonly List<FileNode> _files = [];

    public void AddFile(FileNode file)
    {
        file.Group = fileGroup;
        _files.Add(file);
    }
}